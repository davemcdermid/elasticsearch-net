using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Elasticsearch.Net;
using Nest;
using Tests.Framework;
using Tests.XPack.Security;

namespace Tests.Framework.Integration
{
	public class ElasticsearchNode : IDisposable
	{
		private static readonly object _lock = new object();
		// <installpath> <> <plugin folder prefix>
		private readonly Dictionary<string, Func<string, string>> SupportedPlugins = new Dictionary<string, Func<string, string>>
		{
			{ "delete-by-query", _ => "delete-by-query" },
			{ "cloud-azure", _ => "cloud-azure" },
			{ "mapper-attachments", MapperAttachmentPlugin.GetVersion },
			{ "mapper-murmur3", _ => "mapper-murmur3" },
			{ "license", _ => "license" },
			{ "graph", _ => "graph" },
			{ "shield", _ => "shield" },
			{ "watcher", _ => "watcher" },
		};
		private string[] DefaultNodeSettings { get; }

		private readonly bool _doNotSpawnIfAlreadyRunning;
		private readonly bool _shieldEnabled;
		private readonly bool _watcherEnabled;
		private ObservableProcess _process;
		private IDisposable _processListener;

		public string Version { get; set; }
		public ElasticsearchVersionInfo VersionInfo { get; }
		public string Binary { get; }

		private string RoamingFolder { get; }
		private string RoamingClusterFolder { get; }

		public string TypeOfCluster { get; }
		public bool Started { get; private set; }
		public bool RunningIntegrations { get; private set; }
		public string ClusterName { get; }
		public string NodeName { get; }
		public string RepositoryPath { get; private set; }
		public ElasticsearchNodeInfo Info { get; private set; }
		public int Port { get; private set; }


		private TimeSpan HandleTimeout { get; } = TimeSpan.FromMinutes(1);

#if DOTNETCORE
		// Investigate  problem with ManualResetEvent on CoreClr
		// Maybe due to .WaitOne() not taking exitContext?
		public class Signal
		{
			private readonly object _lock = new object();
			private bool _notified;

			public Signal(bool initialState)
			{
				_notified = initialState;
			}

			public void Set()
			{
				lock (_lock)
				{
					if (!_notified)
					{
						_notified = true;
						Monitor.Pulse(_lock);
					}
				}
			}

			public bool WaitOne(TimeSpan timeout, bool exitContext)
			{
				lock (_lock)
				{
					bool exit = true;
					if (!_notified)
						exit = Monitor.Wait(_lock, timeout);
					return exit;
				}
			}
		}

		private readonly Subject<Signal> _blockingSubject = new Subject<Signal>();
		public IObservable<Signal> BootstrapWork { get; }
#else
		private readonly Subject<ManualResetEvent> _blockingSubject = new Subject<ManualResetEvent>();
		public IObservable<ManualResetEvent> BootstrapWork { get; }
#endif

		public ElasticsearchNode(
			string elasticsearchVersion,
			bool runningIntegrations,
			bool doNotSpawnIfAlreadyRunning,
			string name,
			bool shieldEnabled,
			bool watcherEnabled)
		{
			this._doNotSpawnIfAlreadyRunning = doNotSpawnIfAlreadyRunning;
			this._shieldEnabled = shieldEnabled;
			this._watcherEnabled = watcherEnabled;

			this.TypeOfCluster = name;
			var prefix = name.ToLowerInvariant();
			var suffix = Guid.NewGuid().ToString("N").Substring(0, 6);
			this.ClusterName = $"{prefix}-cluster-{suffix}";
			this.NodeName = $"{prefix}-node-{suffix}";

			this.VersionInfo = new ElasticsearchVersionInfo(runningIntegrations ? elasticsearchVersion : "0.0.0-unittest");
			this.Version = this.VersionInfo.Version + (this.VersionInfo.IsSnapshot ? $"-{VersionInfo.SnapshotIdentifier}" : string.Empty);
			this.RunningIntegrations = runningIntegrations;

			this.BootstrapWork = _blockingSubject;

			var appData = GetApplicationDataDirectory();
			this.RoamingFolder = Path.Combine(appData, "NEST", this.Version);
			this.RoamingClusterFolder = Path.Combine(this.RoamingFolder, "elasticsearch-" + this.VersionInfo.Version);
			this.RepositoryPath = Path.Combine(RoamingFolder, "repositories");
			this.Binary = Path.Combine(this.RoamingClusterFolder, "bin", "elasticsearch") + ".bat";

			var attr = this.VersionInfo.ParsedVersion.Major >= 5 ? "attr." : "";
			this.DefaultNodeSettings = new[]
			{
				$"es.cluster.name={this.ClusterName}",
				$"es.node.name={this.NodeName}",
				$"es.path.repo=\"{this.RepositoryPath}\"",
				$"es.script.inline=on",
				$"es.script.indexed=on",
				$"es.http.compression=true",
				$"es.node.{attr}testingcluster=true",
				$"es.shield.enabled=" + (shieldEnabled ? "true" : "false")
			};

			if (!runningIntegrations)
			{
				this.Port = 9200;
				return;
			}

		}

		private object _lockGetClient = new object { };
		private IElasticClient _client;
		public IElasticClient Client
		{
			get
			{
				if (!this.Started && TestClient.Configuration.RunIntegrationTests)
				{

					throw new Exception("can not request a client from an ElasticsearchNode if that node hasn't started yet");
				}

				if (this._client != null) return this._client;

				lock (_lockGetClient)
				{
					if (this._client != null) return this._client;

					var port = this.Started ? this.Port : 9200;
					this._client = TestClient.GetClient(ComposeSettings, port);
					return this.Client;
				}
			}
		}

		public IObservable<ElasticsearchMessage> Start(string[] additionalSettings = null)
		{
			Console.WriteLine($"Started node {this.NodeName} which {(this.RunningIntegrations ? "will" : "WONT")} spawn an elasticsearch process");
			if (!this.RunningIntegrations) return Observable.Empty<ElasticsearchMessage>();

			this.DownloadAndExtractElasticsearch();
			this.Stop();

			var settingMarker = this.VersionInfo.ParsedVersion.Major >= 5 ? "-E " : "-D";
			var settings = DefaultNodeSettings
				.Concat(additionalSettings ?? Enumerable.Empty<string>())
				.Select(s => $"{settingMarker}{s}")
				.ToList();

			var easyRunBat = Path.Combine(this.RoamingFolder, $"run-{this.TypeOfCluster.ToLowerInvariant()}.bat");
			if (!File.Exists(easyRunBat))
			{
				var badSettings = new[] { "node.name", "cluster.name" };
				var batSettings = string.Join(" ", settings.Where(s => !badSettings.Any(s.Contains)));
				File.WriteAllText(easyRunBat, $@"elasticsearch-{this.Version}\bin\elasticsearch.bat {batSettings}");
			}

#if DOTNETCORE
			var handle = new Signal(false);
#else
			var handle = new ManualResetEvent(false);
#endif
			var alreadyRunning = UseAlreadyRunningInstance(handle);
			if (alreadyRunning != null) return alreadyRunning;

			this._process = new ObservableProcess(this.Binary, settings.ToArray());


			var sb = new StringBuilder();
			var observable = Observable.Using(() => this._process, process => process.Start())
				.Select(consoleLine =>
				{
					if (!this.Started)
					{
						Console.WriteLine(consoleLine);
						sb.AppendLine(consoleLine);
					}
					return new ElasticsearchMessage(consoleLine);
				});
			this._processListener = observable.Subscribe(onNext: s => HandleConsoleMessage(s, handle));

			if (handle.WaitOne(this.HandleTimeout, true)) return observable;

			this.Stop();
			throw new Exception($"Could not start elasticsearch within {this.HandleTimeout}\r\n" + sb.ToString());
		}

#if DOTNETCORE
		private IObservable<ElasticsearchMessage> UseAlreadyRunningInstance(Signal handle)
#else
		private IObservable<ElasticsearchMessage> UseAlreadyRunningInstance(ManualResetEvent handle)
#endif
		{
			if (!_doNotSpawnIfAlreadyRunning) return null;

			var client = TestClient.Default;
			var alreadyUp = client.RootNodeInfo();

			if (!alreadyUp.IsValid) return null;

			var checkPlugins = client.CatPlugins();

			var missingPlugins = SupportedPlugins.Keys.Except(checkPlugins.Records.Select(r => r.Component)).ToList();
			if (missingPlugins.Any())
				throw new Exception($"Already running elasticsearch missed the following plugin(s): {string.Join(", ", missingPlugins)}.");

			this.Started = true;
			this.Port = 9200;
			this.Info = new ElasticsearchNodeInfo(alreadyUp.Version.Number, null, alreadyUp.Version.LuceneVersion);
			this._blockingSubject.OnNext(handle);
			if (!handle.WaitOne(this.HandleTimeout, true))
				throw new Exception($"Could not launch tests on already running elasticsearch within {this.HandleTimeout}");

			ValidateLicense();

			return Observable.Empty<ElasticsearchMessage>();
		}

		private void ValidateLicense()
		{
			var client = TestClient.GetClient();
			var license = client.GetLicense();
			if (license.IsValid && license.License.Status == LicenseStatus.Active) return;

			var exceptionMessageStart = "Server has license plugin installed, ";
#if DOTNETCORE
			var licenseFile = string.Empty;
#else
			var licenseFile = Environment.GetEnvironmentVariable("ES_LICENSE_FILE", EnvironmentVariableTarget.Machine);
#endif

			if (!string.IsNullOrWhiteSpace(licenseFile))
			{
				var putLicense = client.PostLicense(new PostLicenseRequest { License = License.LoadFromDisk(licenseFile) });
				if (!putLicense.IsValid)
					throw new Exception("Server has invalid license and the ES_LICENSE_FILE failed to register\r\n" + putLicense.DebugInformation);

				license = client.GetLicense();
				if (license.IsValid && license.License.Status == LicenseStatus.Active) return;
				exceptionMessageStart += " but the installed license is invalid and we attempted to register ES_LICENSE_FILE ";
			}

			if (license.ApiCall.Success && license.License == null)
				throw new Exception($"{exceptionMessageStart}  but the license was deleted!");

			if (license.License.Status == LicenseStatus.Expired)
				throw new Exception($"{exceptionMessageStart} but the license has expired!");

			if (license.License.Status == LicenseStatus.Invalid)
				throw new Exception($"{exceptionMessageStart} but the license is invalid!");


		}

#if DOTNETCORE
		private void HandleConsoleMessage(ElasticsearchMessage s, Signal handle)
#else
		private void HandleConsoleMessage(ElasticsearchMessage s, ManualResetEvent handle)
#endif
		{
			//no need to snoop for metadata if we already started
			if (!this.RunningIntegrations || this.Started) return;

			ElasticsearchNodeInfo info;
			int port;

			if (s.TryParseNodeInfo(out info))
			{
				this.Info = info;
			}
			else if (s.TryGetStartedConfirmation())
			{
				var client = this._shieldEnabled
					? TestClient.GetClient(settings => settings.BasicAuthentication(ShieldInformation.Admin.Username, ShieldInformation.Admin.Password), port: this.Port)
					: TestClient.GetClient(port: this.Port);

				var healthyCluster = client.ClusterHealth(g => g.WaitForStatus(WaitForStatus.Yellow).Timeout(TimeSpan.FromSeconds(30)));
				if (healthyCluster.IsValid)
				{
					this.Started = true;
					this._blockingSubject.OnNext(handle);
				}
				else
				{
					this._blockingSubject.OnError(new Exception($"Did not see a healthy {this.NodeName} node after the node started for 30 seconds"));
					handle.Set();
					this.Stop();
				}

			}
			else if (s.TryGetPortNumber(out port))
			{
				this.Port = port;
			}
		}

		private void DownloadAndExtractElasticsearch()
		{
			lock (_lock)
			{
				var localZip = Path.Combine(this.RoamingFolder, this.VersionInfo.Zip);

				Directory.CreateDirectory(this.RoamingFolder);
				if (!File.Exists(localZip))
				{
					Console.WriteLine($"Download elasticsearch: {this.VersionInfo.Version} from {this.VersionInfo.DownloadUrl}");
					new WebClient().DownloadFile(this.VersionInfo.DownloadUrl, localZip);
					Console.WriteLine($"Downloaded elasticsearch: {this.VersionInfo.Version}");
				}

				if (!Directory.Exists(this.RoamingClusterFolder))
				{
					Console.WriteLine($"Unzipping elasticsearch: {this.VersionInfo.Version} ...");
					ZipFile.ExtractToDirectory(localZip, this.RoamingFolder);
				}

				var easyRunBat = Path.Combine(this.RoamingClusterFolder, "run.bat");
				if (!File.Exists(easyRunBat))
				{
					File.WriteAllText(easyRunBat, @"bin\elasticsearch.bat ");
				}
				InstallPlugins();
				EnsureShieldAdmin();
				EnsureWatcherActionConfigurations();

				//hunspell config
				var hunspellFolder = Path.Combine(this.RoamingClusterFolder, "config", "hunspell", "en_US");
				var hunspellPrefix = Path.Combine(hunspellFolder, "en_US");
				if (!File.Exists(hunspellPrefix + ".dic"))
				{
					Directory.CreateDirectory(hunspellFolder);
					File.WriteAllText(hunspellPrefix + ".dic", "1\r\nabcdegf");
					File.WriteAllText(hunspellPrefix + ".aff", "SET UTF8\r\nSFX P Y 1\r\nSFX P 0 s");
				}

				var analysFolder = Path.Combine(this.RoamingClusterFolder, "config", "analysis");
				if (!Directory.Exists(analysFolder)) Directory.CreateDirectory(analysFolder);
				var fopXml = Path.Combine(analysFolder, "fop") + ".xml";
				if (!File.Exists(fopXml)) File.WriteAllText(fopXml, "<languages-info />");
				var customStems = Path.Combine(analysFolder, "custom_stems") + ".txt";
				if (!File.Exists(customStems)) File.WriteAllText(customStems, "");
				var stopwords = Path.Combine(analysFolder, "stopwords") + ".txt";
				if (!File.Exists(stopwords)) File.WriteAllText(stopwords, "");
			}
		}

		private void InstallPlugins()
		{
			var pluginCommand = "plugin";
			if (this.VersionInfo.ParsedVersion.Major >= 5) pluginCommand = "elasticsearch-plugin";

			var pluginBat = Path.Combine(this.RoamingClusterFolder, "bin", pluginCommand) + ".bat";
			foreach (var plugin in SupportedPlugins)
			{
				var installPath = plugin.Key;
				var command = plugin.Value(this.VersionInfo.Version);
				var pluginFolder = Path.Combine(this.RoamingClusterFolder, "plugins", installPath);

				if (!Directory.Exists(this.RoamingClusterFolder)) continue;

				// assume plugin already installed
				if (Directory.Exists(pluginFolder)) continue;

				Console.WriteLine($"Installing elasticsearch plugin: {installPath} ...");
				var timeout = TimeSpan.FromSeconds(120);
				var handle = new ManualResetEvent(false);
				Task.Run(() =>
				{
					using (var p = new ObservableProcess(pluginBat, "install", command))
					{
						var o = p.Start();
						Console.WriteLine($"Calling: {pluginBat} install {command}");
						o.Subscribe(Console.WriteLine,
							(e) =>
							{
								Console.WriteLine($"Failed installing elasticsearch plugin: {command}");
								handle.Set();
								throw e;
							},
							() =>
							{
								Console.WriteLine($"Finished installing elasticsearch plugin: {installPath} exit code: {p.ExitCode}");
								handle.Set();
							});
						if (!handle.WaitOne(timeout, true))
							throw new Exception($"Could not install {command} within {timeout}");
					}
				});
				if (!handle.WaitOne(timeout, true))
					throw new Exception($"Could not install {command} within {timeout}");
			}
		}

		private void EnsureShieldAdmin()
		{
			if (!this._shieldEnabled) return;

			var pluginBat = Path.Combine(this.RoamingClusterFolder, "bin", "shield", "esusers") + ".bat";
			foreach (var cred in ShieldInformation.AllUsers)
			{
				var processInfo = new ProcessStartInfo
				{
					FileName = pluginBat,
					Arguments = $"useradd {cred.Username} -p {cred.Password} -r {cred.Role}",
					CreateNoWindow = true,
					UseShellExecute = false,
					RedirectStandardOutput = false,
					RedirectStandardError = false,
					RedirectStandardInput = false
				};
				var p = Process.Start(processInfo);
				p.WaitForExit();
			}
		}

		private void EnsureWatcherActionConfigurations()
		{
			if (!this._watcherEnabled) return;

			var elasticsearchConfig = Path.Combine(this.RoamingClusterFolder, "config", "elasticsearch.yml");
			var lines = File.ReadAllLines(elasticsearchConfig).ToList();
			var saveFile = false;

			// set up for Watcher HipChat action
			if (!lines.Any(line => line.StartsWith("watcher.actions.hipchat.service:")))
			{
				lines.AddRange(new[]
				{
					string.Empty,
					"watcher.actions.hipchat.service:",
					"  account:",
					"    notify-monitoring:",
					"      profile: user",
					"      user: watcher-user@example.com",
					"      auth_token: hipchat_auth_token",
					string.Empty
				});

				saveFile = true;
			}

			// set up for Watcher Slack action
			if (!lines.Any(line => line.StartsWith("watcher.actions.slack.service:")))
			{
				lines.AddRange(new[]
				{
					string.Empty,
					"watcher.actions.slack.service:",
					"  account:",
					"    monitoring:",
					"      url: https://hooks.slack.com/services/foo/bar/baz",
					string.Empty
				});

				saveFile = true;
			}

			// set up for Watcher PagerDuty action
			if (!lines.Any(line => line.StartsWith("watcher.actions.pagerduty.service:")))
			{
				lines.AddRange(new[]
				{
					string.Empty,
					"watcher.actions.pagerduty.service:",
					"  account:",
					"    my_pagerduty_account:",
					"      service_api_key: pager_duty_service_api_key",
					string.Empty
				});

				saveFile = true;
			}

			if (saveFile) File.WriteAllLines(elasticsearchConfig, lines);
		}

		private string GetApplicationDataDirectory()
		{
#if DOTNETCORE
			return Environment.GetEnvironmentVariable("APPDATA");
#else
			return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#endif
		}

		private ConnectionSettings ComposeSettings(ConnectionSettings s) => AddBasicAuthentication(AppendClusterNameToHttpHeaders(s));
		private ConnectionSettings AddBasicAuthentication(ConnectionSettings settings)
		{
			if (!_shieldEnabled) return settings;
			return settings.BasicAuthentication("es_admin", "es_admin");
		}
		private ConnectionSettings AppendClusterNameToHttpHeaders(ConnectionSettings settings)
		{
			IConnectionConfigurationValues values = settings;
			var headers = values.Headers ?? new NameValueCollection();
			headers.Add("ClusterName", this.ClusterName);
			return settings;
		}

		private readonly object _stopLock = new object();
		public void Stop(bool disposing = false)
		{
			lock (_stopLock)
			{
				if (_disposed) return;

				var hasStarted = this.Started;
				this.Started = false;

				if (disposing)
				{
					this._disposed = true;
					Console.WriteLine($"Disposing node {this.NodeName} which {(hasStarted ? "" : "never ")}spawned an actual elasticsearch process");
				}

				this._process?.Dispose();
				this._processListener?.Dispose();

				if (this.Info?.Pid != null)
				{
					var esProcess = Process.GetProcessById(this.Info.Pid.Value);
					Console.WriteLine($"Killing elasticsearch PID {this.Info.Pid}");
					esProcess.Kill();
					esProcess.WaitForExit(5000);
					esProcess.Close();
				}

				if (!this.RunningIntegrations || !hasStarted) return;

				Console.WriteLine($"Node had started on port: {this.Port} cleaning up log/data/repository files...");

				if (this._doNotSpawnIfAlreadyRunning) return;
				var dataFolder = Path.Combine(this.RoamingClusterFolder, "data", this.ClusterName);
				if (Directory.Exists(dataFolder))
				{
					Directory.Delete(dataFolder, true);
				}

				var logPath = Path.Combine(this.RoamingClusterFolder, "logs");
				var files = Directory.GetFiles(logPath, this.ClusterName + "*.log");
				foreach (var f in files)
				{
					File.Delete(f);
				}

				if (Directory.Exists(this.RepositoryPath))
				{
					Directory.Delete(this.RepositoryPath, true);
				}
			}
		}

		private bool _disposed;
		public void Dispose() => this.Stop(disposing: true);
	}
}
