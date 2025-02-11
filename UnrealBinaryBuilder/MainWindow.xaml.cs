﻿
using HandyControl.Controls;
using HandyControl.Data;
using HandyControl.Themes;
using HandyControl.Tools;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UnrealBinaryBuilder.Classes;
using UnrealBinaryBuilder.UserControls;
using UnrealBinaryBuilderUpdater;

namespace UnrealBinaryBuilder
{
	public partial class MainWindow
	{
		private static string GetProductVersionString()
		{
			Version ProductVersion = Assembly.GetEntryAssembly().GetName().Version;
			string ReturnValue = $"{ProductVersion.Major}.{ProductVersion.Minor}";

			if (ProductVersion.Build > 0)
			{
				ReturnValue += $".{ProductVersion.Build}";
			}

			if (ProductVersion.Revision > 0)
			{
				ReturnValue += $".{ProductVersion.Revision}";
			}

			return ReturnValue;
		}

		private static readonly string SetupBatFileName = "Setup.bat";
		private static readonly string GenerateProjectBatFileName = "GenerateProjectFiles.bat";
		private static readonly string AUTOMATION_TOOL_NAME = "AutomationToolLauncher";
		private static readonly string DEFAULT_BUILD_XML_FILE = "Engine/Build/InstalledEngineBuild.xml";
		private Process CurrentProcess = null;

		private int NumErrors = 0;
		private int NumWarnings = 0;

		private int CompiledFiles = 0;
		private int CompiledFilesTotal = 0;

		private bool bIsBuilding = false;
		private bool bLastBuildSuccess = false;

		private string LogMessage = null;
		private string FinalBuildPath = null;

		public string CurrentTheme = null;
		public PostBuildSettings postBuildSettings = null;

		private readonly Stopwatch StopwatchTimer = new Stopwatch();
		private readonly DispatcherTimer DispatchTimer = new DispatcherTimer();

		public BuilderSettingsJson SettingsJSON = null;

		private string AutomationExePath = null;		

		private PluginCard CurrentPluginBeingBuilt = null;
		private List<string> PluginBuildEnginePath = new List<string>();
		private Dialog aboutDialog = null;
		private Dialog downloadDialog = null;
		private DownloadDialog downloadDialogWindow = null;
		private static UBBUpdater unrealBinaryBuilderUpdater = null;
		private bool bUpdateAvailable = false;

		public enum ZipLogInclusionType
		{
			FileIncluded,
			FileSkipped,
			ExtensionSkipped
		}

		private enum CurrentProcessType
		{
			None,
			SetupBat,
			GenerateProjectFiles,
			BuildAutomationTool,
			BuildUnrealEngine,
			BuildPlugin
		}

		private CurrentProcessType currentProcessType = CurrentProcessType.None;

		public MainWindow()
		{
			InitializeComponent();
			GameAnalyticsCSharp.InitializeGameAnalytics(GetProductVersionString(), this);
			AddLogEntry($"Welcome to Unreal Binary Builder v{GetProductVersionString()}");
			PluginQueueBtn.IsEnabled = false;
			postBuildSettings = new PostBuildSettings(this);
			SettingsJSON = BuilderSettings.GetSettingsFile(true);
			BuilderSettings.LoadInitialValues();
			DataContext = SettingsJSON;
			bUse2019Compiler.IsEnabled = false;

			if (Plugins.GetInstalledEngines() == null)
			{
				PluginsTab.Visibility = Visibility.Collapsed;
				AddLogEntry("Could not find any installed Engine versions. Plugins tab is disabled.", true);
				ShowToastMessage("Could not find any installed Engine versions. Plugins tab is disabled.", LogViewer.EMessageType.Error);
			}
			else
			{
				foreach (var p in Plugins.GetInstalledEngines())
				{
					string RunUATFile = Path.Combine(p.Value, "Engine", "Build", "BatchFiles", "RunUAT.bat");
					if (File.Exists(RunUATFile))
					{
						PluginEngineVersionSelection.Items.Add(p.Key);
						PluginBuildEnginePath.Add(p.Value);
					}
					else
					{
						AddLogEntry($"{p.Key} will not be available for Plugin build. RunUAT.bat does not exist in {Path.GetDirectoryName(RunUATFile)}.", true);
					}
				}
			}

			if (File.Exists(AutomationExePath) && Path.GetFileNameWithoutExtension(AutomationExePath) == AUTOMATION_TOOL_NAME)
			{
				BuildRocketUE.IsEnabled = true;
			}

			ChangeStatusLabel("Idle.");

			DispatchTimer.Tick += new EventHandler(DispatchTimer_Tick);
			DispatchTimer.Interval = new TimeSpan(0, 0, 1);

			CurrentTheme = SettingsJSON.Theme;
			if (CurrentTheme.ToLower() == "violet")
			{
				ShowToastMessage("Violet theme is not fully supported yet.", LogViewer.EMessageType.Warning, true, false, "Important", 4);
				UpdateSkin(SkinType.Violet);				
			}
			else if (CurrentTheme.ToLower() == "dark")
			{
				UpdateSkin(SkinType.Dark);
			}
			else
			{
				ShowToastMessage("Default theme is not fully supported yet.", LogViewer.EMessageType.Warning, true, false, "Important", 4);
				UpdateSkin(SkinType.Default);
			}
			ZipStatusLabel.Visibility = Visibility.Visible;
			ZipStausStackPanel.Visibility = Visibility.Collapsed;

			if (SettingsJSON.bCheckForUpdatesAtStartup)
			{
				CheckForUpdates();
			}
		}

		public static void OpenBrowser(string InURL)
		{
			InURL = InURL.Replace("&", "^&");
			Process.Start(new ProcessStartInfo("cmd", $"/c start {InURL}") { CreateNoWindow = true });
		}

		private void CheckForUpdates()
		{
			if (CurrentProcess == null)
			{
				CheckUpdateBtn.IsEnabled = false;
				CheckUpdateBtn.Content = "Checking...";
				if (unrealBinaryBuilderUpdater == null)
				{
					unrealBinaryBuilderUpdater = new UBBUpdater();
				}

				GameAnalyticsCSharp.AddDesignEvent("Update:Check");
				unrealBinaryBuilderUpdater.SilentUpdateFinishedEventHandler += OnUpdateCheck;
				unrealBinaryBuilderUpdater.CheckForUpdatesSilently();
			}
		}

		public void ShowToastMessage(string Message, LogViewer.EMessageType ToastType = LogViewer.EMessageType.Info, bool bShowCloseButton = true, bool bStaysOpen = false, string Token = "", int WaitTime = 3)
		{
			Growl.Clear(Token);
			GrowlInfo growlInfo = new GrowlInfo()
			{
				ShowDateTime = false,
				ShowCloseButton = bShowCloseButton,
				StaysOpen = bStaysOpen,
				Token = Token,
				WaitTime = WaitTime
			};

			growlInfo.Message = Message;
			switch (ToastType)
			{
				case LogViewer.EMessageType.Info:
					Growl.Info(growlInfo);
					break;
				case LogViewer.EMessageType.Warning:
					Growl.Warning(growlInfo);
					break;
				case LogViewer.EMessageType.Error:
					Growl.Error(growlInfo);
					break;
			}
		}

		private void OnUpdateCheck(object sender, UpdateProgressFinishedEventArgs e)
		{
			CheckUpdateBtn.Content = "Check for Update";
			switch (e.appUpdateCheckStatus)
			{
				case AppUpdateCheckStatus.UpdateAvailable:
					bUpdateAvailable = true;
					CheckUpdateBtn.Content = $"Install Update {e.castItem.Version}";
					ShowToastMessage($"Update {e.castItem.Version} is available.", LogViewer.EMessageType.Info, true, false, "", 2);
					break;
				case AppUpdateCheckStatus.NoUpdate:
					ShowToastMessage("You are running the latest version.", LogViewer.EMessageType.Info, true, false, "", 2);
					break;
				case AppUpdateCheckStatus.CouldNotDetermine:
					ShowToastMessage("Failed to determine update settings. Please try again later.", LogViewer.EMessageType.Error);
					break;
				case AppUpdateCheckStatus.UserSkip:
					break;
			}
			CheckUpdateBtn.IsEnabled = true;
		}

		private void ChangeStatusLabel(string InStatus)
		{
			StatusLabel.Text = GetCurrentProcessName() != null ? $"Status: Running [{GetCurrentProcessName()} - {InStatus}]" : $"Status: {InStatus}";
		}

		private void ChangeStepLabel(string current, string total)
		{
			Dispatcher.Invoke(() => { StepLabel.Text = $"Step: [{current}/{total}] "; });
		}

		private string GetConditionalString(bool? bCondition)
		{
			return (bool)bCondition ? "true" : "false";
		}

		private void DispatchTimer_Tick(object sender, EventArgs e)
		{
			ChangeStatusLabel(string.Format("Building... Time Elapsed: {0:hh\\:mm\\:ss}", StopwatchTimer.Elapsed));
		}

		public void AddZipLog(string InMessage, ZipLogInclusionType InType)
		{
			LogEntry logEntry = new LogEntry();
			logEntry.Message = InMessage;
			LogControl.AddZipLog(logEntry, InType);
		}

		public void AddLogEntry(string InMessage, bool bIsError = false)
		{
			if (InMessage != null)
			{
				LogEntry logEntry = new LogEntry();
				logEntry.Message = InMessage;

				LogViewer.EMessageType InMessageType = bIsError ? LogViewer.EMessageType.Error : LogViewer.EMessageType.Info;

				if (bIsError == false)
				{
					const string StepPattern = @"\*{6} \[(\d+)\/(\d+)\]";
					const string WarningPattern = @"warning|\*\*\* Unable to determine ";
					const string DebugPattern = @".+\*\s\D\d\D\d\D\s\w+|.+\*\sFor\sUE4";
					const string ErrorPattern = @"Error_Unknown|ERROR|exited with code 1";
					const string ProcessedFilesPattern = @"\w.+\.(cpp|cc|c|h|ispc)";

					Regex StepRgx = new Regex(StepPattern, RegexOptions.IgnoreCase);
					Regex WarningRgx = new Regex(WarningPattern, RegexOptions.IgnoreCase);
					Regex DebugRgx = new Regex(DebugPattern, RegexOptions.IgnoreCase);
					Regex ErrorRgx = new Regex(ErrorPattern, RegexOptions.IgnoreCase);
					Regex ProcessedFilesRgx = new Regex(ProcessedFilesPattern, RegexOptions.IgnoreCase);

					if (StepRgx.IsMatch(InMessage))
					{
						GroupCollection captures = StepRgx.Match(InMessage).Groups;
						ChangeStepLabel(captures[1].Value, captures[2].Value);
						CompiledFiles = 0;
					}

					if (ProcessedFilesRgx.IsMatch(InMessage))
					{
						CompiledFiles++;
						CompiledFilesTotal++;
						Dispatcher.Invoke(() => 
						{ 
							ProcessedFilesLabel.Text = $"[Compiled: {CompiledFiles}. Total: {CompiledFilesTotal}]"; 
						});
					}

					if (WarningRgx.IsMatch(InMessage))
					{
						NumWarnings++;
						InMessageType = LogViewer.EMessageType.Warning;
					}
					else if (ErrorRgx.IsMatch(InMessage) && InMessage.Contains("ShadowError") == false)
					{
						NumErrors++;
						InMessageType = LogViewer.EMessageType.Error;
					}
					else if (DebugRgx.IsMatch(InMessage))
					{
						InMessageType = LogViewer.EMessageType.Debug;
					}
				}

				LogControl.AddLogEntry(logEntry, InMessageType);
				LogMessage += InMessage + "\r\n";
			}
		}

		private void Internal_ShutdownWindows()
		{
			Process.Start("shutdown", "/s /t 5");
			Application.Current.Shutdown();
		}

		private void SaveAllSettings()
		{
			BuilderSettings.SaveSettings();
		}

		private void UnrealBinaryBuilderWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
		{
			if (bIsBuilding)
			{
				if (HandyControl.Controls.MessageBox.Show($"{GetCurrentProcessName()} is still running. Would you like to stop it and exit?", "Build in progress", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
				{
					GameAnalyticsCSharp.AddDesignEvent($"Build:{GetCurrentProcessName()}:Killed:ExitProgram");
					CurrentProcess.Kill();
					CurrentProcess.Close();
					CurrentProcess.Dispose();
					CurrentProcess = null;
				}
				else
				{
					e.Cancel = true;
					return;
				}
			}

			GameAnalyticsCSharp.EndSession();
			SaveAllSettings();

			Application.Current.Shutdown();
		}

		private void CurrentProcess_OutputDataReceived(object sender, DataReceivedEventArgs e)
		{
			AddLogEntry(e.Data);
		}

		private void CurrentProcess_ErrorDataReceived(object sender, DataReceivedEventArgs e)
		{
			NumErrors++;
			AddLogEntry(e.Data, true);
		}

		private void CurrentProcess_Exited(object sender, EventArgs e)
		{
			DispatchTimer.Stop();
			StopwatchTimer.Stop();
			bLastBuildSuccess = CurrentProcess.ExitCode == 0;
			AddLogEntry(string.Format($"{GetCurrentProcessName()} exited with code {0}\n", CurrentProcess.ExitCode.ToString()));

			Dispatcher.Invoke(() =>
			{
				BuildRocketUE.Content = "Build Unreal Engine";
				ChangeStatusLabel(string.Format("Build finished with code {0}. {1} errors, {2} warnings. Time elapsed: {3:hh\\:mm\\:ss}", CurrentProcess.ExitCode, NumErrors, NumWarnings, StopwatchTimer.Elapsed));
			});

			CurrentProcess.Close();
			CurrentProcess.Dispose();
			CurrentProcess = null;

			NumErrors = 0;
			NumWarnings = 0;
			AddLogEntry("========================== BUILD FINISHED ==========================");
			AddLogEntry(string.Format("Compiled approximately {0} files.", CompiledFilesTotal));
			AddLogEntry(string.Format("Took {0:hh\\:mm\\:ss}", StopwatchTimer.Elapsed));
			AddLogEntry(string.Format("Build ended at {0}", DateTime.Now.ToString("dddd, dd MMMM yyyy HH:mm:ss")));
			StopwatchTimer.Reset();
			Dispatcher.Invoke(() =>
			{
				StartSetupBatFile.IsEnabled = true;
				StartPluginBuildsBtn.IsEnabled = true;
				OnBuildFinished(bLastBuildSuccess);
			});
		}

		private void OnBuildFinished(bool bBuildSucess)
		{
			ZipStatusLabel.Content = "Idle";
			bIsBuilding = false;
			if (bBuildSucess)
			{
				switch (currentProcessType)
				{
					case CurrentProcessType.BuildUnrealEngine:
						if (postBuildSettings.CanSaveToZip())
						{
							EngineTabControl.SelectedIndex = 1;
							if (FinalBuildPath == null)
							{
								FinalBuildPath = Path.GetFullPath(AutomationExePath).Replace(@"\Engine\Binaries\DotNET", @"\LocalBuilds\Engine").Replace(Path.GetFileName(AutomationExePath), "");
								GameAnalyticsCSharp.LogEvent("Final Build Path was null. Fixed.", GameAnalyticsSDK.Net.EGAErrorSeverity.Info);
							}
							AddLogEntry($"Creating ZIP file. Installed build can be found in {FinalBuildPath}");
							postBuildSettings.PrepareToSave();
							postBuildSettings.SaveToZip(FinalBuildPath, ZipPath.Text);
							AddLogEntry($"Saving zip file to {ZipPath.Text}");
							WriteToLogFile();
							return;
						}
						break;
					case CurrentProcessType.SetupBat:
						GameAnalyticsCSharp.AddProgressEnd("Build", "Setup");
						GenerateProjectFiles();
						break;
					case CurrentProcessType.GenerateProjectFiles:
						GameAnalyticsCSharp.AddProgressEnd("Build", "ProjectFiles");						
						BuildAutomationToolLauncher();
						break;
					case CurrentProcessType.BuildAutomationTool:
						GameAnalyticsCSharp.AddProgressEnd("Build", "AutomationTool");					
						if (bContinueToEngineBuild.IsChecked == true)
						{
							BuildEngine();
						}
						break;
				}
			}

			if (currentProcessType == CurrentProcessType.BuildPlugin)
			{
				GameAnalyticsCSharp.AddProgressEnd("Build", "Plugin");
				CurrentPluginBeingBuilt.PluginFinishedBuild(bBuildSucess);
				CurrentPluginBeingBuilt = null;
				foreach (var C in PluginQueues.Children)
				{
					PluginCard pluginCard = (PluginCard)C;
					if (pluginCard.IsPending())
					{
						BuildPlugin(pluginCard);
						break;
					}
				}

				if (CurrentPluginBeingBuilt == null)
				{
					Growl.Clear("PluginBuild");
					ShowToastMessage($"Finished plugin queue build with {PluginQueues.Children.Count} plugin(s)");
				}
			}

			WriteToLogFile();
			TryShutdown();
		}

		public void TryShutdown()
		{
			if (currentProcessType == CurrentProcessType.BuildUnrealEngine)
			{
				if (bShutdownWindows.IsChecked == true)
				{
					if (bShutdownIfSuccess.IsChecked == true)
					{
						if (bLastBuildSuccess)
						{
							GameAnalyticsCSharp.AddDesignEvent("Shutdown:BuildState:Success");
							Internal_ShutdownWindows();
						}
						else
						{
							GameAnalyticsCSharp.AddDesignEvent("Shutdown:BuildState:Failed");
						}
					}
					else
					{
						GameAnalyticsCSharp.AddDesignEvent("Shutdown:Started");
						Internal_ShutdownWindows();
					}
				}
			}
		}

		private void BrowseEngineFolder_Click(object sender, RoutedEventArgs e)
		{
			System.Windows.Forms.FolderBrowserDialog NewFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
			NewFolderDialog.ShowDialog();
			SetupBatFilePath.Text = NewFolderDialog.SelectedPath;
			if (TryUpdateAutomationExePath() == false)
			{
				HandyControl.Controls.MessageBox.Error($"This is not the Unreal Engine root folder.\n\nPlease select the root folder where {SetupBatFileName} and {GenerateProjectBatFileName} exists.", "Incorrect folder");
			}
		}

		private bool TryUpdateAutomationExePath()
		{
			bool bRequiredFilesExist = false;
			if (string.IsNullOrEmpty(AutomationToolPath.Text) == false && string.IsNullOrEmpty(AutomationExePath))
			{
				AutomationExePath = AutomationToolPath.Text;
				if (Path.GetFileNameWithoutExtension(AutomationExePath) == AUTOMATION_TOOL_NAME)
				{
					SetupBatFilePath.Text = AutomationExePath.Replace(Path.GetFileName(AutomationExePath), "").Replace(@"\Engine\Binaries\DotNET", "");
					bRequiredFilesExist = File.Exists(Path.Combine(SetupBatFilePath.Text, SetupBatFileName)) && File.Exists(Path.Combine(SetupBatFilePath.Text, GenerateProjectBatFileName));
				}
				else
				{
					ChangeStatusLabel("Error. Invalid automation tool file selected.");
					HandyControl.Controls.MessageBox.Error("This is not Automation Tool Launcher. Please select AutomationToolLauncher.exe", "");
				}
			}
			else
			{
				bRequiredFilesExist = File.Exists(Path.Combine(SetupBatFilePath.Text, SetupBatFileName)) && File.Exists(Path.Combine(SetupBatFilePath.Text, GenerateProjectBatFileName));
				StartSetupBatFile.IsEnabled = bRequiredFilesExist;
				if (bRequiredFilesExist)
				{
					AutomationExePath = Path.Combine(SetupBatFilePath.Text, "Engine", "Binaries", "DotNET", $"{AUTOMATION_TOOL_NAME}.exe");
					AutomationToolPath.Text = AutomationExePath;
				}
			}

			return bRequiredFilesExist;
		}

		private string SetupBatCommandLineArgs()
		{
			string CommandLines = "--force";

			if (SettingsJSON.GitDependencyAll == true)
			{
				CommandLines += " --all";
			}

			foreach (GitPlatform gp in SettingsJSON.GitDependencyPlatforms)
			{
				if (gp.bIsIncluded == false)
				{
					CommandLines += $" --exclude={gp.Name}";
				}
			}

			CommandLines += $" --threads={SettingsJSON.GitDependencyThreads}";
			CommandLines += $" --max-retries={SettingsJSON.GitDependencyMaxRetries}";
			
			if (SettingsJSON.GitDependencyEnableCache == false)
			{
				CommandLines += " --no-cache";
			}
			else if (string.IsNullOrEmpty(SettingsJSON.GitDependencyCache) == false)
			{
				CommandLines += $" --cache={SettingsJSON.GitDependencyCache.Replace("\\", "/")}";
				CommandLines += $" --cache-size-multiplier={SettingsJSON.GitDependencyCacheMultiplier}";
				CommandLines += $" --cache-days={SettingsJSON.GitDependencyCacheDays}";
			}

			if (string.IsNullOrEmpty(SettingsJSON.GitDependencyProxy) == false)
			{
				CommandLines += $" --proxy={SettingsJSON.GitDependencyProxy}";
			}


			return CommandLines;
		}

		private void StartSetupBatFile_Click(object sender, System.Windows.RoutedEventArgs e)
		{
			bool bRequiredFilesExist = File.Exists(Path.Combine(SetupBatFilePath.Text, SetupBatFileName)) && File.Exists(Path.Combine(SetupBatFilePath.Text, GenerateProjectBatFileName));
			if (bRequiredFilesExist == false)
			{
				HandyControl.Controls.MessageBox.Error($"This is not the Unreal Engine root folder.\n\nPlease select the root folder where {SetupBatFileName} and {GenerateProjectBatFileName} exists.", "Incorrect folder");
				return;
			}

			if (bIsBuilding == false)
			{
				bIsBuilding = true;
				string Commandline = SetupBatCommandLineArgs();
				ProcessStartInfo processStartInfo = new ProcessStartInfo
				{
					FileName = Path.Combine(SetupBatFilePath.Text, SetupBatFileName),
					Arguments = Commandline,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardError = true,
					RedirectStandardOutput = true
				};

				currentProcessType = CurrentProcessType.SetupBat;
				CreateProcess(processStartInfo);
				AddLogEntry($"Commandline: {Commandline}");
				ChangeStatusLabel("Building...");
				GameAnalyticsCSharp.AddProgressStart("Build", "Setup");
			}
		}

		private void CreateProcess(ProcessStartInfo processStartInfo, bool bClearLogs = true)
		{
			if (File.Exists(processStartInfo.FileName))
			{
				StartSetupBatFile.IsEnabled = false;
				DispatchTimer.Start();
				StopwatchTimer.Start();

				CompiledFiles = CompiledFilesTotal = 0;
				ProcessedFilesLabel.Text = "[Compiled: 0. Total: 0]";

				if (bClearLogs)
				{
					LogControl.ClearAllLogs();
					AddLogEntry($"Welcome to UE4 Binary Builder v{GetProductVersionString()}");
				}

				AddLogEntry($"========================== RUNNING - {Path.GetFileName(processStartInfo.FileName)} ==========================");

				CurrentProcess = new Process();
				CurrentProcess.StartInfo = processStartInfo;
				CurrentProcess.EnableRaisingEvents = true;
				CurrentProcess.OutputDataReceived += new DataReceivedEventHandler(CurrentProcess_OutputDataReceived);
				CurrentProcess.ErrorDataReceived += new DataReceivedEventHandler(CurrentProcess_ErrorDataReceived);
				CurrentProcess.Exited += new EventHandler(CurrentProcess_Exited);
				CurrentProcess.Start();
				CurrentProcess.BeginErrorReadLine();
				CurrentProcess.BeginOutputReadLine();
			}
			else
			{
				AddLogEntry($"File does not exist: {processStartInfo.FileName}", true);
				ShowToastMessage($"File does not exist: {Path.GetFileName(processStartInfo.FileName)}", LogViewer.EMessageType.Error);
			}
		}

		private string GetCurrentProcessName()
		{
			if (CurrentProcess != null)
			{
				return CurrentProcess.ProcessName;
			}

			return null;
		}

		private void WriteToLogFile()
		{
			BuilderSettings.WriteToLogFile(LogMessage);
		}

		private void UpdateSkin(SkinType skin)
		{
			SharedResourceDictionary.SharedDictionaries.Clear();
			Resources.MergedDictionaries.Add(ResourceHelper.GetSkin(skin));
			Resources.MergedDictionaries.Add(new ResourceDictionary
			{
				Source = new Uri("pack://application:,,,/HandyControl;component/Themes/Theme.xaml")
			});
			Application.Current.MainWindow?.OnApplyTemplate();
			GameAnalyticsCSharp.AddDesignEvent($"Theme:{skin.ToString()}");
		}

		private void AutomationToolBrowse_Click(object sender, RoutedEventArgs e)
		{
			OpenFileDialog NewFileDialog = new OpenFileDialog
			{
				Filter = "exe file (*.exe)|*.exe"
			};

			ChangeStatusLabel(string.Format("Waiting for {0}.exe", AUTOMATION_TOOL_NAME));
			if (NewFileDialog.ShowDialog() == true)
			{
				AutomationExePath = NewFileDialog.FileName;
				AutomationToolPath.Text = AutomationExePath;
				if (Path.GetFileNameWithoutExtension(AutomationExePath) == AUTOMATION_TOOL_NAME)
				{
					BuildRocketUE.IsEnabled = true;
					ChangeStatusLabel("Idle.");
					FinalBuildPath = Path.GetFullPath(AutomationExePath).Replace(@"\Engine\Binaries\DotNET", @"\LocalBuilds\Engine").Replace(Path.GetFileName(AutomationExePath), "");
					AddLogEntry(string.Format("Binary build can be found at: {0}", FinalBuildPath));
				}
				else
				{
					GameAnalyticsCSharp.AddDesignEvent($"AutomationTool:IncorrectName:{Path.GetFileNameWithoutExtension(AutomationExePath)}");
					ChangeStatusLabel("Error. Invalid automation tool file selected.");
					HandyControl.Controls.MessageBox.Error("This is not Automation Tool Launcher. Please select AutomationToolLauncher.exe", "");
				}

				return;
			}

			ChangeStatusLabel("Idle.");
		}

		private void CustomBuildXMLBrowse_Click(object sender, RoutedEventArgs e)
		{
			OpenFileDialog NewFileDialog = new OpenFileDialog
			{
				Filter = "xml file (*.xml)|*.xml"
			};

			ChangeStatusLabel("Waiting for custom build file...");
			if (NewFileDialog.ShowDialog() == true)
			{
				CustomBuildXMLFile.Text = NewFileDialog.FileName;
				CustomOptions.IsEnabled = true;
				GameAnalyticsCSharp.AddDesignEvent($"BuildXML:Custom:{NewFileDialog.FileName}");
			}

			ChangeStatusLabel("Idle.");
		}

		private void ResetDefaultBuildXML_Click(object sender, RoutedEventArgs e)
		{
			CustomBuildXMLFile.Text = DEFAULT_BUILD_XML_FILE;
			GameAnalyticsCSharp.AddDesignEvent("BuildXML:ResetToDefault");
		}

		private string PrepareCommandline()
		{
			string BuildXMLFile = CustomBuildXMLFile.Text;
			if (CustomBuildXMLFile.Text == "")
			{
				BuildXMLFile = DEFAULT_BUILD_XML_FILE;
			}

			if (BuildXMLFile != DEFAULT_BUILD_XML_FILE)
			{
				BuildXMLFile = string.Format("\"{0}\"", CustomBuildXMLFile.Text);				
			}

			if (GameConfigurations.Text == "")
			{
				GameConfigurations.Text = "Development;Shipping";
				GameAnalyticsCSharp.AddDesignEvent("CommandLine:GameConfiguration:Reset");
			}

			string CommandLineArgs = string.Format("BuildGraph -target=\"Make Installed Build Win64\" -script={0} -set:WithDDC={1} -set:SignExecutables={2} -set:EmbedSrcSrvInfo={3} -set:GameConfigurations={4} -set:WithFullDebugInfo={5} -set:HostPlatformEditorOnly={6} -set:AnalyticsTypeOverride={7}",
					BuildXMLFile,
					GetConditionalString(bWithDDC.IsChecked),
					GetConditionalString(bSignExecutables.IsChecked),
					GetConditionalString(bEnableSymStore.IsChecked),
					GameConfigurations.Text,
					GetConditionalString(bWithFullDebugInfo.IsChecked),
					GetConditionalString(bHostPlatformEditorOnly.IsChecked),
					AnalyticsOverride.Text);

			if (bWithDDC.IsChecked == true && bHostPlatformDDCOnly.IsChecked == true)
			{
				CommandLineArgs += " -set:HostPlatformDDCOnly=true";
			}

			if (bHostPlatformOnly.IsChecked == true)
			{
				CommandLineArgs += " -set:HostPlatformOnly=true";
				GameAnalyticsCSharp.AddDesignEvent("CommandLine:HostOnly");
			}
			else
			{
				CommandLineArgs += string.Format(" -set:WithWin64={0} -set:WithWin32={1} -set:WithMac={2} -set:WithAndroid={3} -set:WithIOS={4} -set:WithTVOS={5} -set:WithLinux={6} -set:WithLumin={7}",
						GetConditionalString(bWithWin64.IsChecked),
						GetConditionalString(bWithWin32.IsChecked),
						GetConditionalString(bWithMac.IsChecked),
						GetConditionalString(bWithAndroid.IsChecked),
						GetConditionalString(bWithIOS.IsChecked),
						GetConditionalString(bWithTVOS.IsChecked),
						GetConditionalString(bWithLinux.IsChecked),
						GetConditionalString(bWithLumin.IsChecked));

				if (SupportHTML5())
				{
					CommandLineArgs += string.Format(" -set:WithHTML5={0}",
					GetConditionalString(bWithHTML5.IsChecked));
				}

				if (SupportConsoles())
				{
					CommandLineArgs += string.Format(" -set:WithSwitch={0} -set:WithPS4={1} -set:WithXboxOne={2}",
					GetConditionalString(bWithSwitch.IsChecked),
					GetConditionalString(bWithPS4.IsChecked),
					GetConditionalString(bWithXboxOne.IsChecked));
				}

				if (SupportLinuxAArch64())
				{
					CommandLineArgs += string.Format(" -set:WithLinuxAArch64={0}", GetConditionalString(bWithLinuxAArch64.IsChecked));
				}
			}

			if (IsEngineSelection425OrAbove())
			{
				CommandLineArgs += string.Format(" -set:CompileDatasmithPlugins={0} -set:VS2019={1}",
					GetConditionalString(bCompileDatasmithPlugins.IsChecked),
					GetConditionalString(bVS2019.IsChecked));
			}

			if (EngineVersionSelection.SelectedIndex > 1)
			{
				CommandLineArgs += string.Format(" -set:WithServer={0} -set:WithClient={1} -set:WithHoloLens={2}",
					GetConditionalString(bWithServer.IsChecked),
					GetConditionalString(bWithClient.IsChecked),
					GetConditionalString(bWithHololens.IsChecked));
			}

			if (BuildXMLFile != DEFAULT_BUILD_XML_FILE && CustomOptions.Text != string.Empty)
			{
				CommandLineArgs += string.Format(" {0}", CustomOptions.Text);
				AddLogEntry("Using custom options...");
				GameAnalyticsCSharp.AddDesignEvent("CommandLine:UsingCustomOptions");
			}

			if (bCleanBuild.IsChecked == true)
			{
				CommandLineArgs += " -Clean";
				GameAnalyticsCSharp.AddDesignEvent("CommandLine:CleanEnabled");
			}

			return CommandLineArgs;
		}

		private void BuildRocketUE_Click(object sender, RoutedEventArgs e)
		{
			BuildEngine();
		}

		private void BuildEngine()
		{
			if (bIsBuilding)
			{
				MessageBoxResult MessageResult;
				switch (currentProcessType)
				{
					case CurrentProcessType.SetupBat:
					case CurrentProcessType.GenerateProjectFiles:
						MessageResult = HandyControl.Controls.MessageBox.Show("Automation tool is currently running. Would you like to stop it and start building the Engine?\n\nPress Yes to force stop Automation Tool and begin Engine Build.\nPress No to continue current process.", "Automation Tool Running!", MessageBoxButton.YesNo, MessageBoxImage.Question);
						switch (MessageResult)
						{
							case MessageBoxResult.Yes:
								GameAnalyticsCSharp.AddDesignEvent("Build:AutomationTool:Killed");
								CurrentProcess.Kill();
								break;
							case MessageBoxResult.No:
								return;
						}
						break;
					case CurrentProcessType.BuildUnrealEngine:
						MessageResult = HandyControl.Controls.MessageBox.Show("Unreal Engine is being compiled right now. Do you want to stop it?", "Compiling Engine", MessageBoxButton.YesNo, MessageBoxImage.Question);
						if (MessageResult == MessageBoxResult.Yes)
						{
							GameAnalyticsCSharp.AddDesignEvent("Build:AutomationTool:UnrealEngine:Killed");
							CurrentProcess.Kill(true);
							return;
						}
						break;
				}
			}

			TryUpdateAutomationExePath();
			EngineTabControl.SelectedIndex = 2;
			currentProcessType = CurrentProcessType.BuildUnrealEngine;
			bLastBuildSuccess = false;

			if (FinalBuildPath == null && string.IsNullOrWhiteSpace(AutomationExePath) == false)
			{
				FinalBuildPath = Path.GetFullPath(AutomationExePath).Replace(@"\Engine\Binaries\DotNET", @"\LocalBuilds\Engine").Replace(Path.GetFileName(AutomationExePath), "");
			}

			if (Directory.Exists(FinalBuildPath))
			{
				MessageBoxResult MessageResult = HandyControl.Controls.MessageBox.Show($"Looks like an Engine build is already available at {FinalBuildPath}. Would you like to skip compiling the Engine and start zipping the existing build?\n\nPress Yes to Skip Engine build and start zipping (if enabled).\nPress No to continue with Engine Build.\nPress Cancel to do nothing.", "Zip Binary Version", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
				switch (MessageResult)
				{
					case MessageBoxResult.Yes:
						GameAnalyticsCSharp.AddDesignEvent("Build:EngineExists:FinishBuild");
						// We don't want the system to shutdown since user is interacting.
						bool? bOriginalShutdownState = bShutdownWindows.IsChecked;
						bShutdownWindows.IsChecked = false;
						OnBuildFinished(true);
						bShutdownWindows.IsChecked = bOriginalShutdownState;
						return;
					case MessageBoxResult.Cancel:
						GameAnalyticsCSharp.AddDesignEvent("Build:EngineExists:Exit");
						return;
					default:
						GameAnalyticsCSharp.AddDesignEvent("Build:EngineExists:IgnoreAndContinue");
						break;
				}
			}

			if (EngineVersionSelection.SelectedIndex == 0)
			{
				HandyControl.Controls.MessageBox.Fatal("Please select your Engine version to build. If you are unsure about the version number look into the following file:\n\n/Engine/Source/Runtime/Launch/Resources/Version.h\n\nAnd check ENGINE_MAJOR_VERSION and ENGINE_MINOR_VERSION.", "Select Engine Version.");
				return;
			}

			ChangeStatusLabel("Preparing to build...");

			if (postBuildSettings.ShouldSaveToZip() && postBuildSettings.DirectoryIsWritable(ZipPath.Text) == false)
			{
				GameAnalyticsCSharp.AddDesignEvent("Build:ZipEnabled:InvalidSetting");
				HandyControl.Controls.MessageBox.Error(string.Format("You chose to save Engine build as a zip file but below directory is either not available or not writable.\n\n{0}", ZipPath.Text), "Error");
				return;
			}

			if (CustomBuildXMLFile.Text == string.Empty)
			{
				CustomBuildXMLFile.Text = DEFAULT_BUILD_XML_FILE;
			}
			else if (CustomBuildXMLFile.Text != DEFAULT_BUILD_XML_FILE)
			{
				if (File.Exists(CustomBuildXMLFile.Text) == false)
				{
					GameAnalyticsCSharp.LogEvent("BuildXML does not exist.", GameAnalyticsSDK.Net.EGAErrorSeverity.Error);
					ChangeStatusLabel("Error. Build xml does not exist.");
					HandyControl.Controls.MessageBox.Error(string.Format("Build XML {0} does not exist!", CustomBuildXMLFile.Text), "Error");
					return;
				}
			}

			if (SupportHTML5() == false && bWithHTML5.IsChecked == true)
			{
				GameAnalyticsCSharp.AddDesignEvent($"Build:HTML5:IncorrectEngine:{GetEngineName()}");
				bWithHTML5.IsChecked = false;
				if (SettingsJSON.bShowHTML5DeprecatedMessage)
				{
					HandyControl.Controls.MessageBox.Show("HTML5 support was removed from Unreal Engine 4.24 and higher. You had it enabled but since it is of no use, it is disabled.");
				}
			}

			if (SupportConsoles() == false && (bWithSwitch.IsChecked == true || bWithPS4.IsChecked == true || bWithXboxOne.IsChecked == true))
			{
				GameAnalyticsCSharp.AddDesignEvent($"Build:Console:IncorrectEngine:{GetEngineName()}");
				bWithSwitch.IsChecked = bWithPS4.IsChecked = bWithXboxOne.IsChecked = false;
				if (SettingsJSON.bShowConsoleDeprecatedMessage)
				{
					HandyControl.Controls.MessageBox.Show("Console support was removed from Unreal Engine 4.25 and higher. You had it enabled but since it is of no use, it is disabled.");
				}
			}

			bool bContinueToBuild = true;
			if (SettingsJSON.bEnableEngineBuildConfirmationMessage)
			{
				bContinueToBuild = HandyControl.Controls.MessageBox.Show("You are going to build a binary version of Unreal Engine 4. This is a long process and might take time to finish. Are you sure you want to continue? ", "Build Binary Version", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
			}

			if (bContinueToBuild)
			{
				if (bWithDDC.IsChecked == true && SettingsJSON.bEnableDDCMessages)
				{
					MessageBoxResult MessageResult = HandyControl.Controls.MessageBox.Show("Building Derived Data Cache (DDC) is one of the slowest aspect of the build. You can skip this step if you want to. Do you want to continue with DDC enabled?\n\nPress Yes to continue with build\nPress No to continue without DDC\nPress Cancel to stop build", "Warning", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

					switch (MessageResult)
					{
						case MessageBoxResult.No:
							bWithDDC.IsChecked = false;
							GameAnalyticsCSharp.AddDesignEvent("Build:DDC:AutoDisabled");
							break;
						case MessageBoxResult.Cancel:
							GameAnalyticsCSharp.AddDesignEvent("Build:DDC:Exit");
							return;
						default:
							GameAnalyticsCSharp.AddDesignEvent("Build:DDC:IgnoreAndContinue");
							break;
					}
				}

				GameAnalyticsCSharp.AddDesignEvent($"Build:Engine:{GetEngineName()}");
				BuildRocketUE.Content = "Stop Build";
				BuildRocketUE.IsEnabled = true;
				string CommandLineArgs = PrepareCommandline();

				ProcessStartInfo AutomationStartInfo = new ProcessStartInfo
				{
					FileName = AutomationExePath,
					Arguments = CommandLineArgs,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardError = true,
					RedirectStandardOutput = true
				};

				CreateProcess(AutomationStartInfo);

				bIsBuilding = true;
				ChangeStatusLabel("Building...");
				ZipStatusLabel.Content = "Waiting for Engine to finish building...";
				GameAnalyticsCSharp.AddDesignEvent("Build:Started");
			}
		}

		private void EngineVersionSelection_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
		{
			bWithServer.IsEnabled = bWithClient.IsEnabled = EngineVersionSelection.SelectedIndex > 1;
			bWithHTML5.IsEnabled = SupportHTML5();
			bWithLinuxAArch64.IsEnabled = SupportLinuxAArch64();
			bWithSwitch.IsEnabled = bWithPS4.IsEnabled = bWithXboxOne.IsEnabled = SupportConsoles();
			bCompileDatasmithPlugins.IsEnabled = bVS2019.IsEnabled = IsEngineSelection425OrAbove();
		}

		private bool SupportHTML5()
		{
			return EngineVersionSelection.SelectedIndex < 3;
		}

		private bool SupportLinuxAArch64()
		{
			return EngineVersionSelection.SelectedIndex >= 3;
		}

		private bool SupportConsoles()
		{
			return EngineVersionSelection.SelectedIndex <= 3;
		}

		private bool IsEngineSelection425OrAbove()
		{
			return EngineVersionSelection.SelectedIndex >= 4;
		}

		private string GetEngineName()
		{
			string ReturnString = "Unknown";
			switch (EngineVersionSelection.SelectedIndex)
			{
				case 1:
					ReturnString = "4.22";
					break;
				case 2:
					ReturnString = "4.23";
					break;
				case 3:
					ReturnString = "4.24";
					break;
				case 4:
					ReturnString = "4.25";
					break;
				case 5:
					ReturnString = "4.26";
					break;
			}

			return ReturnString;
		}

		private void CopyCommandLine_Click(object sender, RoutedEventArgs e)
		{
			GameAnalyticsCSharp.AddDesignEvent("CommandLine:CopyToClipboard");
			Clipboard.SetDataObject(PrepareCommandline());
			HandyControl.Controls.MessageBox.Show("Command line copied to clipboard!");
		}

		private void SetZipPathLocation_Click(object sender, RoutedEventArgs e)
		{
			System.Windows.Forms.SaveFileDialog SFD = new System.Windows.Forms.SaveFileDialog();
			SFD.DefaultExt = ".zip";
			SFD.Filter = "Zip File (.zip)|*.zip";
			System.Windows.Forms.DialogResult SaveDialogResult = SFD.ShowDialog();
			if (SaveDialogResult == System.Windows.Forms.DialogResult.OK)
			{
				ZipPath.Text = SFD.FileName;
			}
		}

		private void GenerateProjectFiles()
		{
			if (bIsBuilding == false)
			{
				bIsBuilding = true;
				BuildRocketUE.IsEnabled = false;
				ProcessStartInfo processStartInfo = new ProcessStartInfo
				{
					FileName = Path.Combine(SetupBatFilePath.Text, GenerateProjectBatFileName),
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardError = true,
					RedirectStandardOutput = true
				};

				currentProcessType = CurrentProcessType.GenerateProjectFiles;
				CreateProcess(processStartInfo, false);
				ChangeStatusLabel("Building...");
				GameAnalyticsCSharp.AddProgressStart("Build", "ProjectFiles");
			}
		}

		private bool? BuildAutomationToolLauncher()
		{
			if (bIsBuilding == false)
			{
				if (string.IsNullOrEmpty(AutomationExePath))
				{
					if (TryUpdateAutomationExePath() == false)
					{
						AddLogEntry("Failed to build Automation Tool. AutomationExePath was null.", true);
						return null;
					}
				}

				bIsBuilding = true;
				BuildRocketUE.IsEnabled = false;
				currentProcessType = CurrentProcessType.BuildAutomationTool;
				if (File.Exists(AutomationExePath))
				{
					AddLogEntry("Skip building Automation Tool. Already exists.");
					OnBuildFinished(true);
					return false;
				}

				string RunUATFile = Path.Combine(SetupBatFilePath.Text, "Engine", "Build", "BatchFiles", "RunUAT.bat");
				if (File.Exists(RunUATFile))
				{
					ProcessStartInfo processStartInfo = new ProcessStartInfo
					{
						FileName = RunUATFile,
						Arguments = "-compileonly",
						UseShellExecute = false,
						CreateNoWindow = true,
						RedirectStandardError = true,
						RedirectStandardOutput = true
					};

					CreateProcess(processStartInfo, false);
					ChangeStatusLabel("Building...");
					GameAnalyticsCSharp.AddProgressStart("Build", "AutomationTool");
					return true;
				}
			}

			return null;
		}

		private string BuildPlugin(PluginCard pluginCard)
		{
			if (bIsBuilding == false)
			{
				if (pluginCard.IsValid())
				{
					CurrentPluginBeingBuilt = pluginCard;
					bIsBuilding = true;
					BuildRocketUE.IsEnabled = false;
					StartPluginBuildsBtn.IsEnabled = false;
					currentProcessType = CurrentProcessType.BuildPlugin;
					AddLogEntry($"========================== BUILDING PLUGIN {Path.GetFileNameWithoutExtension(pluginCard.PluginPath).ToUpper()} ==========================");
					AddLogEntry($"Plugin: {pluginCard.PluginPath}");
					AddLogEntry($"Package Location: {pluginCard.DestinationPath}");
					AddLogEntry($"Target Engine: {pluginCard.EngineVersionText.Text}");
					ProcessStartInfo processStartInfo = new ProcessStartInfo
					{
						FileName = pluginCard.RunUATFile,
						Arguments = $"BuildPlugin -Plugin=\"{pluginCard.PluginPath}\" -Package=\"{pluginCard.DestinationPath}\" -Rocket {pluginCard.GetCompiler()} {pluginCard.GetTargetPlatforms()}",
						UseShellExecute = false,
						CreateNoWindow = true,
						RedirectStandardError = true,
						RedirectStandardOutput = true
					};

					pluginCard.BuildStarted();
					CreateProcess(processStartInfo, false);
					ChangeStatusLabel($"Building Plugin - {Path.GetFileNameWithoutExtension(pluginCard.PluginPath)}");
					ShowToastMessage($"Building Plugin - {Path.GetFileNameWithoutExtension(pluginCard.PluginPath)}", LogViewer.EMessageType.Info, false, true, "PluginBuild");
					GameAnalyticsCSharp.AddProgressStart("Build", "Plugin");					
					return null;
				}

				return $"{pluginCard.PluginName.Text} ({pluginCard.EngineVersionText.Text}) is already compiled.";
			}

			return "Cannot build plugin while task is running";
		}

		private void CancelZipping_Click(object sender, RoutedEventArgs e)
		{
			postBuildSettings.CancelTask();
		}

		private void GitCachePathBrowse_Click(object sender, RoutedEventArgs e)
		{
			System.Windows.Forms.FolderBrowserDialog NewFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
			NewFolderDialog.ShowDialog();
			GitCachePath.Text = NewFolderDialog.SelectedPath;
		}

		private void PluginQueueBtn_Click(object sender, RoutedEventArgs e)
		{
			if (File.Exists(PluginPath.Text) && Directory.Exists(PluginDestinationPath.Text))
			{
				if (PluginEngineVersionSelection.SelectedIndex < 0)
				{
					HandyControl.Controls.MessageBox.Fatal($"Cannot build \"{Path.GetFileNameWithoutExtension(PluginPath.Text)}\". Engine selection is invalid.");
					return;
				}

				List<string> TargetPlatformsList = null;
				if (bPluginOverrideTargetPlatforms.IsChecked == true)
				{
					TargetPlatformsList = new List<string>();					
					foreach (var C in PluginPlatforms.Children)
					{
						if (((CheckBox)C).IsChecked == true)
						{
							TargetPlatformsList.Add(((CheckBox)C).Name.Replace("bPlugin", ""));
						}
					}
				}

				bool bCanUse2019Compiler = bUse2019Compiler.IsEnabled && (bool)bUse2019Compiler.IsChecked;
				PluginQueues.Children.Add(new PluginCard(this, PluginPath.Text, PluginDestinationPath.Text, PluginBuildEnginePath[PluginEngineVersionSelection.SelectedIndex], (bool)bCanUse2019Compiler, TargetPlatformsList, (bool)PluginZip.IsChecked, PluginZipPath.Text, (bool)PluginZipForMarketplace.IsChecked));
				PluginQueueBtn.IsEnabled = false;
				PluginPath.Text = "";
				PluginDestinationPath.Text = "";
				PluginEngineVersionSelection.SelectedIndex = -1;
				PluginZipForMarketplace.IsChecked = true;
				PluginZip.IsChecked = false;
				PluginZipPath.Text = "";
				foreach (var C in PluginPlatforms.Children)
				{
					if (((CheckBox)C).Name != "bPluginWin64")
					{
						((CheckBox)C).IsChecked = false;
					}
				}
			}
			else
			{
				HandyControl.Controls.MessageBox.Fatal($"Cannot build \"{Path.GetFileNameWithoutExtension(PluginPath.Text)}\". Either file does not exist or save location is not valid.");
			}
		}

		private void PluginPathBrowse_Click(object sender, RoutedEventArgs e)
		{
			OpenFileDialog NewFileDialog = new OpenFileDialog
			{
				Filter = "Unreal Plugin file (*.uplugin)|*.uplugin"
			};

			if (NewFileDialog.ShowDialog() == true)
			{
				PluginPath.Text = NewFileDialog.FileName;
				foreach (var C in PluginPlatforms.Children)
				{
					CheckBox checkBox = (CheckBox)C;
					if (checkBox.Name != "bPluginWin64")
					{
						checkBox.IsChecked = false;
					}
				}

				using (StreamReader reader = File.OpenText(PluginPath.Text))
				{
					UE4PluginJson PluginJson = JsonConvert.DeserializeObject<UE4PluginJson>(File.ReadAllText(PluginPath.Text));
					
					if (PluginJson.Modules[0].WhitelistPlatforms != null)
					{
						foreach (var C in PluginPlatforms.Children)
						{
							CheckBox checkBox = (CheckBox)C;
							if (PluginJson.Modules[0].WhitelistPlatforms.Contains(checkBox.Name.Replace("bPlugin", "")))
							{
								checkBox.IsChecked = true;
							}
						}
					}
				}

			}

			if (File.Exists(PluginPath.Text) && Directory.Exists(PluginDestinationPath.Text))
			{
				PluginQueueBtn.IsEnabled = true;
			}
		}

		private void PluginDestinationPathBrowse_Click(object sender, RoutedEventArgs e)
		{
			System.Windows.Forms.FolderBrowserDialog NewFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
			NewFolderDialog.ShowDialog();
			PluginDestinationPath.Text = NewFolderDialog.SelectedPath;

			if (File.Exists(PluginPath.Text) && Directory.Exists(PluginDestinationPath.Text))
			{
				PluginQueueBtn.IsEnabled = true;
			}
		}

		public void RemovePluginFromList(PluginCard pluginCard)
		{
			PluginQueues.Children.Remove(pluginCard);
		}

		private void StartPluginBuildsBtn_Click(object sender, RoutedEventArgs e)
		{
			if (PluginQueues.Children.Count == 0)
			{
				HandyControl.Controls.MessageBox.Fatal("Queue is empty. Add one or more plugin to queue and build.");
			}
			else
			{
				string PluginBuildMessage = null;
				foreach (var C in PluginQueues.Children)
				{
					PluginCard pluginCard = (PluginCard)C;
					if (pluginCard.IsPending())
					{
						AddLogEntry($"Building {PluginQueues.Children.Count} Plugin(s).");
						AddLogEntry("");
						ShowToastMessage($"Building {PluginQueues.Children.Count} Plugin(s).");
						PluginBuildMessage = BuildPlugin(pluginCard);
						break;
					}
				}

				if (PluginBuildMessage != null)
				{
					Growl.Clear();
					HandyControl.Controls.MessageBox.Fatal(PluginBuildMessage);
				}
			}
		}

		private void PluginEngineVersionSelection_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			try
			{
				bUse2019Compiler.IsEnabled = Convert.ToDouble(PluginEngineVersionSelection.SelectedValue) >= 4.25;
			}
			catch (Exception) {}
		}

		private void PluginZipDestinationPathBrowse_Click(object sender, RoutedEventArgs e)
		{
			System.Windows.Forms.FolderBrowserDialog NewFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
			NewFolderDialog.ShowDialog();
			PluginZipPath.Text = NewFolderDialog.SelectedPath;
		}

		private void GetSourceCode_Click(object sender, RoutedEventArgs e)
		{
			OpenBrowser("https://github.com/ryanjon2040/UE4-Binary-Builder");
			GameAnalyticsCSharp.AddDesignEvent("Menu:Click:SourceCode");
		}

		private void SupportUnrealX_Click(object sender, RoutedEventArgs e)
		{
			OpenBrowser("https://www.buymeacoffee.com/ryanjon2040");
			GameAnalyticsCSharp.AddDesignEvent("Menu:Click:BuyMeACoffee");
		}

		private void SupportAgora_Click(object sender, RoutedEventArgs e)
		{
			OpenBrowser("https://www.patreon.com/ryanjon2040");
			GameAnalyticsCSharp.AddDesignEvent("Menu:Click:Agora");
		}

		private void FeedbackBtn_Click(object sender, RoutedEventArgs e)
		{
			OpenBrowser("https://forms.gle/LeZqAeqmV9fWQpxP7");
			GameAnalyticsCSharp.AddDesignEvent("Menu:Click:Feedback");
		}
		private void ChangelogBtn_Click(object sender, RoutedEventArgs e)
		{
			OpenBrowser("https://github.com/ryanjon2040/UE4-Binary-Builder/blob/master/CHANGELOG.md");
			GameAnalyticsCSharp.AddDesignEvent("Menu:Click:Changelog");
		}

		private void OpenLogFolderBtn_Click(object sender, RoutedEventArgs e)
		{
			BuilderSettings.OpenLogFolder();
		}

		private void OpenSettingsBtn_Click(object sender, RoutedEventArgs e)
		{
			BuilderSettings.OpenSettings();
		}

		private void AboutBtn_Click(object sender, RoutedEventArgs e)
		{
			GameAnalyticsCSharp.AddDesignEvent("AboutDialog:Open");
			aboutDialog = Dialog.Show(new AboutDialog(this));
		}

		public void CloseAboutDialog()
		{
			GameAnalyticsCSharp.AddDesignEvent("AboutDialog:Close");
			aboutDialog.Close();
		}

		private void CheckUpdateBtn_Click(object sender, RoutedEventArgs e)
		{
			if (CurrentProcess == null)
			{
				if (bUpdateAvailable)
				{
					downloadDialogWindow = new DownloadDialog(this);
					downloadDialog = Dialog.Show(downloadDialogWindow);
					CheckUpdateBtn.IsEnabled = false;
					CheckUpdateBtn.Content = "Downloading...";
					unrealBinaryBuilderUpdater.UpdateDownloadStartedEventHandler += DownloadUpdateProgressStart;
					unrealBinaryBuilderUpdater.UpdateDownloadFinishedEventHandler += DownloadUpdateProgressFinish;
					unrealBinaryBuilderUpdater.UpdateProgressEventHandler += DownloadUpdateProgress;
					unrealBinaryBuilderUpdater.DownloadUpdate();
				}
				else
				{
					CheckForUpdates();
				}
			}
			else
			{
				ShowToastMessage($"{GetCurrentProcessName()} is currently running. You can check for updates after it is done.", LogViewer.EMessageType.Error);
			}
		}

		private void DownloadUpdateProgressStart(object sender, UpdateProgressDownloadStartEventArgs e)
		{
			GameAnalyticsCSharp.AddDesignEvent($"Update:Download:{e.Version}");
			downloadDialogWindow.Initialize(e.UpdateSize, e.Version);
		}

		private void DownloadUpdateProgress(object sender, UpdateProgressDownloadEventArgs progressDownloadEventArgs)
		{
			downloadDialogWindow.SetProgress(progressDownloadEventArgs.AppUpdateProgress);
		}

		private void DownloadUpdateProgressFinish(object sender, EventArgs e)
		{
			GameAnalyticsCSharp.AddDesignEvent($"Update:Install:{downloadDialogWindow.VersionText}");
			unrealBinaryBuilderUpdater.UpdateDownloadStartedEventHandler -= DownloadUpdateProgressStart;
			unrealBinaryBuilderUpdater.UpdateDownloadFinishedEventHandler -= DownloadUpdateProgressFinish;
			unrealBinaryBuilderUpdater.UpdateProgressEventHandler -= DownloadUpdateProgress;
			unrealBinaryBuilderUpdater.CloseApplicationEventHandler += CloseApplication;
			unrealBinaryBuilderUpdater.InstallUpdate();
		}

		private void CloseApplication(object sender, EventArgs e)
		{
			downloadDialog.Close();
			Close();
		}

		private void GitPlatform_CheckedChanged(object sender, RoutedEventArgs e)
		{
			string TargetPlatformName = ((Control)sender).Name.Replace("Git", "").Replace("Platform", "");
			BuilderSettings.UpdatePlatformInclusion(TargetPlatformName, (bool)((CheckBox)sender).IsChecked);
		}
	}
}
