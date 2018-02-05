using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using fastJSON;
using Renci.SshNet;

namespace DotnetSshDeploy
{
	internal class Deploy
	{
		#region Private data

		private bool quietMode;
		private bool verboseMode;
		private bool encryptPasswordMode;
		private string configFileName;
		private string profileName;
		private ConfigFile configFile;
		private Profile activeProfile;
		private ConnectionInfo connectionInfo;
		private List<FileEntry> localFiles = new List<FileEntry>();
		private List<FileEntry> remoteFiles = new List<FileEntry>();
		private List<FileEntry> localOnlyFiles;
		private List<FileEntry> remoteOnlyFiles;
		private List<FileEntry> localUpdatedFiles;
		private List<FileEntry> filesToUpload;
		private List<string> filesToDelete = new List<string>();
		private string tempUploadDirectory = "";
		private bool profileChanged;
		private string encryptedPasswordPrefix = "$$crypt$$";

		#endregion Private data

		#region Execution wrapper

		public int Execute(string[] args)
		{
			try
			{
				return ExecuteInternal(args);
			}
			catch (AppException ex)
			{
				Console.Error.WriteLine(ex.Message);
				return 1;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Error: An unhandled exception has occurred: {ex.Message}");
				return 1;
			}
			finally
			{
#if DEBUG
				if (System.Diagnostics.Debugger.IsAttached)
				{
					Console.WriteLine("Press any key to exit...");
					Console.ReadKey(true);
				}
#endif
			}
		}

		#endregion Execution wrapper

		public int ExecuteInternal(string[] args)
		{
			ReadArgs(args);
			FindConfigFile();
			ReadConfigFile();
			LoadProfile();
			if (encryptPasswordMode)
				return HandleEncryptPassword();
			if (!quietMode)
				Console.WriteLine($"Deploying to {activeProfile.UserName}@{activeProfile.HostName}:{activeProfile.RemotePath}");

			InitializeConnectionInfo();
			FindLocalFiles();

			using (var sshClient = new SshClient(connectionInfo))
			using (var sftpClient = new SftpClient(connectionInfo))
			{
				Connect(sftpClient);
				ChangeRemoteDirectory(sftpClient);
				FindRemoteFiles(sftpClient);
				if (!HandleNewRemoteOnlyFiles())
					return 2;
				if (profileChanged && !SaveProfile())
					Console.Error.WriteLine("Warning: Profile data will be unchanged at next deployment.");
				if (!filesToUpload.Any() && !filesToDelete.Any())
				{
					if (!quietMode)
						Console.WriteLine("Remote already up-to-date.");
					return 0;
				}

				RunSshCommands(sshClient, "pre-upload", activeProfile.Commands.PreUpload);
				UploadFiles(sftpClient);
				RunSshCommands(sshClient, "pre-install", activeProfile.Commands.PreInstall);
				DeleteFiles(sftpClient);
				CopyUploadedFiles(sshClient);
				RunSshCommands(sshClient, "post-install", activeProfile.Commands.PostInstall);
			}
			if (!quietMode)
				Console.WriteLine("Finished.");
			return 0;
		}

		#region Argument handling methods

		private void ReadArgs(string[] args)
		{
			bool optionMode = true;
			Action<string> nextArgHandler = null;

			foreach (string arg in args)
			{
				if (nextArgHandler != null)
				{
					nextArgHandler(arg);
					nextArgHandler = null;
				}
				else if (arg == "--")
				{
					optionMode = false;
				}
				else if (optionMode && arg.StartsWith("-"))
				{
					switch (arg.Substring(1))
					{
						case "c":
							nextArgHandler = x => configFileName = x;
							break;
						case "e":
							encryptPasswordMode = true;
							break;
						case "q":
							quietMode = true;
							break;
						case "v":
							verboseMode = true;
							break;
						default:
							throw new AppException($"Invalid option: {arg}");
					}
				}
				else if (profileName == null)
				{
					profileName = arg;
				}
				else
				{
					throw new AppException($"Too many arguments: {arg}");
				}
			}
			if (nextArgHandler != null)
			{
				throw new AppException("Missing value after last argument.");
			}

			if (verboseMode) quietMode = false;   // Verbose overrides quiet
		}

		#endregion Argument handling methods

		#region Configuration file methods

		private void FindConfigFile()
		{
			if (!string.IsNullOrWhiteSpace(configFileName))
			{
				configFileName = Environment.ExpandEnvironmentVariables(configFileName);
				if (!File.Exists(configFileName))
				{
					throw new AppException($"Specified config file not found: {configFileName}");
				}
			}
			else
			{
				string testFileName;
				if (File.Exists(testFileName = "sshDeploy.json"))
				{
					configFileName = testFileName;
				}
				else if (File.Exists(testFileName = Path.Combine("Properties", "sshDeploy.json")))
				{
					configFileName = testFileName;
				}
				else
				{
					throw new AppException("No config file found.");
				}
			}
			if (verboseMode)
				Console.WriteLine($"- Using config file: {configFileName}");
		}

		private void ReadConfigFile()
		{
			string json;
			try
			{
				json = File.ReadAllText(configFileName);
			}
			catch (Exception ex)
			{
				throw new AppException($"Error reading config file: {ex.Message}", ex);
			}

			try
			{
				configFile = JSON.ToObject<ConfigFile>(json);
			}
			catch (Exception ex)
			{
				throw new AppException($"Error parsing config file: {ex.Message}", ex);
			}
		}

		private void LoadProfile()
		{
			if (string.IsNullOrWhiteSpace(profileName))
			{
				profileName = configFile.Profiles.FirstOrDefault(kvp => kvp.Value.IsDefault).Key;
			}
			if (string.IsNullOrWhiteSpace(profileName) && configFile.Profiles.Count == 1)
			{
				profileName = configFile.Profiles.Keys.First();
			}
			if (string.IsNullOrWhiteSpace(profileName))
			{
				throw new AppException("No profile specified and no default or single profile available.");
			}

			if (!configFile.Profiles.TryGetValue(profileName, out activeProfile))
			{
				throw new AppException($"Profile \"{profileName}\" is not defined in config file: {configFileName}");
			}
			if (activeProfile.Commands == null)
			{
				activeProfile.Commands = new ProfileCommands();
			}
		}

		private bool SaveProfile(bool throwOnError = false)
		{
			if (verboseMode)
				Console.WriteLine("- Saving config file due to changes");

			// Serialize JSON data
			string json;
			try
			{
				var jsonParams = new JSONParameters
				{
					FormatterIndentSpaces = 2,
					SerializeToCamelCaseNames = true,
					UseExtensions = false,
					UseEscapedUnicode = false
				};
				json = JSON.ToNiceJSON(configFile, jsonParams);
				json = json.TrimEnd() + Environment.NewLine;
			}
			catch (Exception ex)
			{
				if (throwOnError)
					throw new AppException($"Error serializing config file: {ex.Message}", ex);
				Console.Error.WriteLine($"Error serializing config file: {ex.Message}");
				return false;
			}

			// Create backup of existing file
			try
			{
				File.Copy(configFileName, configFileName + ".bak", true);
			}
			catch (Exception ex)
			{
				if (throwOnError)
					throw new AppException($"Error backing up config file: {ex.Message}", ex);
				Console.Error.WriteLine($"Error backing up config file: {ex.Message}");
				return false;
			}

			// Write new config file
			try
			{
				File.WriteAllText(configFileName, json);
			}
			catch (Exception ex)
			{
				if (throwOnError)
					throw new AppException($"Error writing config file (backup created): {ex.Message}", ex);
				Console.Error.WriteLine($"Error writing config file (backup created): {ex.Message}");
				return false;
			}

			// Delete backup file
			try
			{
				File.Delete(configFileName + ".bak");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Error deleting backup config file: {ex.Message}");
				// Don't return false
			}
			return true;
		}

		private int HandleEncryptPassword()
		{
#if !NETCOREAPP2_0
			Console.Write("Password [" + (!string.IsNullOrEmpty(activeProfile.Password) ? "****" : "") + "] (space to delete): ");
			string input = ReadLineMasked();
			if (input == " ")
				activeProfile.Password = "";
			else if (input != "")
				activeProfile.Password = encryptedPasswordPrefix + SshDeploy.CryptoHelper.EncryptWindows(input);
			SaveProfile(throwOnError: true);
			return 0;
#else
			throw new AppException("Encrypted passwords are not supported in this version.");
#endif
		}

		private static string ReadLineMasked()
		{
			string input = "";
			while (true)
			{
				var keyInfo = Console.ReadKey(true);
				if (keyInfo.Key == ConsoleKey.Enter)
				{
					break;
				}
				else if (keyInfo.Key == ConsoleKey.Backspace)
				{
					if (input.Length > 0)
					{
						input = input.Substring(0, input.Length - 1);
						Console.Write("\b \b");
					}
				}
				else if (keyInfo.KeyChar != 0)
				{
					input += keyInfo.KeyChar;
					Console.Write("*");
				}
			}
			return input;
		}

		#endregion Configuration file methods

		#region Connection methods

		private void InitializeConnectionInfo()
		{
			int port = activeProfile.Port != 0 ? activeProfile.Port : 22;
			var authMethods = new List<AuthenticationMethod>();
			if (!string.IsNullOrWhiteSpace(activeProfile.Password))
			{
				string password = activeProfile.Password;
				if (password.StartsWith(encryptedPasswordPrefix))
				{
#if !NETCOREAPP2_0
					password = SshDeploy.CryptoHelper.DecryptWindows(password.Substring(encryptedPasswordPrefix.Length));
#else
					throw new AppException("Encrypted passwords are not supported in this version.");
#endif
				}
				authMethods.Add(new PasswordAuthenticationMethod(activeProfile.UserName, password));
			}
			if (!string.IsNullOrWhiteSpace(activeProfile.KeyFileName))
			{
				string keyFileName = GetAbsolutePath(activeProfile.KeyFileName);
				if (!string.IsNullOrWhiteSpace(keyFileName))
				{
					try
					{
						PrivateKeyFile key;
						if (!string.IsNullOrWhiteSpace(activeProfile.KeyFilePassphrase))
						{
							key = new PrivateKeyFile(keyFileName, activeProfile.KeyFilePassphrase);
						}
						else
						{
							key = new PrivateKeyFile(keyFileName);
						}
						authMethods.Add(new PrivateKeyAuthenticationMethod(activeProfile.UserName, key));
					}
					catch (InvalidOperationException ex)
					{
						throw new AppException($"Error loading the private key, maybe the passphrase is wrong: {ex.Message}", ex);
					}
					catch (Exception ex)
					{
						throw new AppException($"Error loading the private key: {ex.Message}", ex);
					}
				}
			}
			connectionInfo = new ConnectionInfo(activeProfile.HostName, port, activeProfile.UserName, authMethods.ToArray());
		}

		private void Connect(BaseClient client)
		{
			int retry = 10;
			while (retry-- > 0)
			{
				try
				{
					client.Connect();
					if (verboseMode)
						Console.WriteLine($"- {client.GetType().Name} connected");
					return;
				}
				catch (Exception ex)
				{
					Console.Error.WriteLine($"Error connecting to server (retrying): {ex.Message}");
					Thread.Sleep(2000);
				}
			}
			throw new AppException("Giving up connecting to server.");
		}

		#endregion Connection methods

		#region File scanning methods

		private bool IsIgnoredLocalFile(string fileName) =>
			IsIgnoredFile(fileName, activeProfile.IgnoredLocalFiles);

		private bool IsIgnoredRemoteFile(string fileName) =>
			IsIgnoredFile(fileName, activeProfile.IgnoredRemoteFiles);

		private bool IsIgnoredFile(string fileName, IEnumerable<string> ignoredList)
		{
			if (ignoredList != null)
			{
				foreach (string ignored in ignoredList)
				{
					string regex = "^" + Regex.Escape(ignored).Replace(@"\*\*", ".*").Replace(@"\*", @"[^/\\]*").Replace(@"\?", ".") + "$";
					if (Regex.IsMatch(fileName, regex)) return true;
				}
			}
			return false;
		}

		private string GetAbsolutePath(string path)
		{
			path = path
				.Replace('\\', Path.DirectorySeparatorChar)
				.Replace('/', Path.DirectorySeparatorChar);
			path = Environment.ExpandEnvironmentVariables(path);
			if (!Path.IsPathRooted(path))
			{
				string configFileDir = Path.GetDirectoryName(Path.GetFullPath(configFileName));
				path = Path.Combine(configFileDir, path);
			}
			return path;
		}

		private void FindLocalFiles(string relativePath = "")
		{
			try
			{
				string path = Path.Combine(GetAbsolutePath(activeProfile.LocalPath), relativePath);

				foreach (string file in Directory.GetDirectories(path))
				{
					string relativeFile = (relativePath + "/" + Path.GetFileName(file)).TrimStart('/');
					if (IsIgnoredLocalFile(relativeFile + "/"))
						continue;

					localFiles.Add(new FileEntry { Name = relativeFile + "/" });
					FindLocalFiles(relativeFile);
				}
				foreach (string file in Directory.GetFiles(path))
				{
					string relativeFile = (relativePath + "/" + Path.GetFileName(file)).TrimStart('/');
					if (IsIgnoredLocalFile(relativeFile))
						continue;

					var fi = new FileInfo(file);
					localFiles.Add(new FileEntry { Name = relativeFile, UtcTime = fi.LastWriteTimeUtc, Length = fi.Length });
				}
			}
			catch (Exception ex) when (!(ex is AppException))
			{
				throw new AppException($"Error scanning local files: {ex.Message}", ex);
			}
		}

		private void ChangeRemoteDirectory(SftpClient sftpClient)
		{
			try
			{
				sftpClient.ChangeDirectory(activeProfile.RemotePath);
			}
			catch (Exception ex)
			{
				throw new AppException($"Error changing to remote directory: {ex.Message}", ex);
			}
		}

		private void FindRemoteFiles(SftpClient client, string relativePath = "")
		{
			try
			{
				string path = client.WorkingDirectory + "/" + relativePath;
				foreach (var file in client.ListDirectory(path).ToList())
				{
					if (file.Name == "." || file.Name == "..") continue;

					string relativeFile = (relativePath + "/" + file.Name).TrimStart('/');

					if (file.IsDirectory)
					{
						remoteFiles.Add(new FileEntry { Name = relativeFile + "/" });
						FindRemoteFiles(client, relativeFile);
					}
					else
					{
						remoteFiles.Add(new FileEntry { Name = relativeFile, UtcTime = file.LastWriteTimeUtc, Length = file.Length });
					}
				}
			}
			catch (Exception ex) when (!(ex is AppException))
			{
				throw new AppException($"Error scanning remote files: {ex.Message}", ex);
			}

			if (relativePath == "")
			{
				localOnlyFiles = localFiles
					.Where(lf => !remoteFiles.Any(rf => rf.Name == lf.Name))
					.ToList();
				remoteOnlyFiles = remoteFiles
					.Where(rf => !localFiles.Any(lf => lf.Name == rf.Name))
					.ToList();
				localUpdatedFiles = localFiles
					.Where(lf => lf.UtcTime - remoteFiles.FirstOrDefault(rf => rf.Name == lf.Name)?.UtcTime > TimeSpan.FromSeconds(2))
					.ToList();
				filesToUpload = localOnlyFiles
					.Concat(localUpdatedFiles)
					.ToList();
				if (verboseMode)
				{
					Console.WriteLine($"- {localOnlyFiles.Count} local-only, {remoteOnlyFiles.Count} remote-only, {localUpdatedFiles.Count} locally updated files");
					Console.WriteLine($"- {filesToUpload.Count} files to upload");
				}
			}
		}

		private bool HandleNewRemoteOnlyFiles()
		{
			foreach (var file in remoteOnlyFiles.Where(f => !IsIgnoredRemoteFile(f.Name)))
			{
				if (!HandleNewRemoteOnlyFile(file)) return false;
			}
			if (verboseMode)
			{
				var remoteIgnoredFiles = remoteOnlyFiles
					.Where(f => IsIgnoredRemoteFile(f.Name))
					.ToList();
				if (remoteIgnoredFiles.Any())
				{
					Console.WriteLine("- Current remote-only ignored files:");
					foreach (var remoteIgnoredFile in remoteIgnoredFiles)
					{
						if (remoteIgnoredFile.Name.EndsWith("/"))
							Console.WriteLine($"  - {remoteIgnoredFile.Name}");
						else
							Console.WriteLine($"  - {remoteIgnoredFile.Name} ({remoteIgnoredFile.Length:N0} bytes)");
					}
				}
				Console.WriteLine($"- {filesToDelete.Count} files to delete");
			}
			return true;
		}

		private bool HandleNewRemoteOnlyFile(FileEntry file)
		{
			Console.WriteLine($"File only exists in remote: {file.Name}");
			while (true)
			{
				Console.Write("(D)elete, (k)eep once, keep (a)lways, (c)ancel?");
				var key = Console.ReadKey(true);
				switch (key.Key)
				{
					case ConsoleKey.D:
						Console.WriteLine(" d");
						filesToDelete.Add(file.Name);
						break;
					case ConsoleKey.K:
						Console.WriteLine(" k");
						break;
					case ConsoleKey.A:
						Console.WriteLine(" a");
						if (activeProfile.IgnoredRemoteFiles == null)
							activeProfile.IgnoredRemoteFiles = new List<string>();
						activeProfile.IgnoredRemoteFiles.Add(file.Name);
						profileChanged = true;
						break;
					case ConsoleKey.C:
					case ConsoleKey.Escape:
						Console.WriteLine(" c");
						return false;
					default:
						Console.WriteLine();
						continue;
				}
				break;
			}
			return true;
		}

		#endregion File scanning methods

		#region File transfer methods

		private void UploadFiles(SftpClient sftpClient)
		{
			if (!filesToUpload.Any()) return;   // Nothing to do

			tempUploadDirectory = "__upload" + (DateTime.UtcNow.Ticks / 10000);   // Milliseconds
			if (!quietMode)
				Console.WriteLine($"Uploading {filesToUpload.Count} files ({filesToUpload.Sum(f => f.Length)} bytes) to {tempUploadDirectory}");
			var createdDirectories = new HashSet<string>();
			string localPath = GetAbsolutePath(activeProfile.LocalPath);
			foreach (var file in filesToUpload)
			{
				try
				{
					string remoteFile = tempUploadDirectory + "/" + file.Name;
					string[] segments = remoteFile.Split('/');
					for (int i = 1; i < segments.Length; i++)
					{
						string path = string.Join("/", segments.Take(i));
						if (!createdDirectories.Contains(path))
						{
							if (verboseMode)
								Console.WriteLine($"- Creating remote directory {path}");
							sftpClient.CreateDirectory(path);
							createdDirectories.Add(path);
						}
					}

					if (!file.Name.EndsWith("/"))
					{
						if (verboseMode)
							Console.WriteLine($"- Uploading file {file.Name} ({file.Length:N0} bytes)");
						using (var stream = File.OpenRead(Path.Combine(localPath, file.Name)))
						{
							sftpClient.UploadFile(stream, tempUploadDirectory + "/" + file.Name);
						}
						var sftpFile = sftpClient.Get(tempUploadDirectory + "/" + file.Name);
						sftpFile.LastWriteTimeUtc = file.UtcTime;
						sftpFile.UpdateStatus();
					}
				}
				catch (Exception ex)
				{
					throw new AppException($"Error uploading file \"{file.Name}\": {ex.Message}", ex);
				}
			}
		}

		private void DeleteFiles(SftpClient sftpClient)
		{
			if (!filesToDelete.Any()) return;   // Nothing to do

			if (!quietMode)
				Console.WriteLine($"Deleting {filesToDelete.Count} files ({filesToDelete.Sum(f => f.Length)} bytes)");
			foreach (string file in filesToDelete.OrderByDescending(f => f))
			{
				try
				{
					if (verboseMode)
						Console.WriteLine($"- Deleting file {file} ({file.Length:N0} bytes)");
					sftpClient.Delete(file);
				}
				catch (Exception ex)
				{
					throw new AppException($"Error deleting file \"{file}\": {ex.Message}", ex);
				}
			}
		}

		private void CopyUploadedFiles(SshClient sshClient)
		{
			if (filesToUpload.Any())
			{
				string commandText = $"cp -prvT \"{activeProfile.RemotePath}/{tempUploadDirectory}\" \"{activeProfile.RemotePath}\"";
				if (!RunSshCommands(sshClient, "copy", new[] { commandText }, throwOnError: false, showName: false))
					throw new AppException("New files could not be copied.");
				commandText = $"rm -r \"{activeProfile.RemotePath}/{tempUploadDirectory}\"";
				if (!RunSshCommands(sshClient, "delete", new[] { commandText }, throwOnError: false, showName: false))
					Console.Error.WriteLine("Uploaded temporary files could not be deleted.");
			}
		}

		#endregion File transfer methods

		#region Command execution methods

		private bool RunSshCommands(SshClient client, string name, IEnumerable<string> commands, bool throwOnError = true, bool showName = true)
		{
			if (commands?.Any() != true) return true;   // Nothing to do

			string capitalisedName = name.Length > 1 ? name.Substring(0, 1).ToUpper() + name.Substring(1) : name.ToUpper();
			if (!client.IsConnected)
				Connect(client);
			if (!quietMode && showName || verboseMode)
				Console.WriteLine($"Running {name} commands");

			foreach (string commandText in commands)
			{
				try
				{
					if (verboseMode)
						Console.WriteLine("$ " + commandText);
					var command = client.RunCommand(commandText);
					if (command.ExitStatus != 0 || verboseMode)
					{
						Console.Error.Write(command.Result);
						Console.Error.Write(command.Error);
					}
					if (command.ExitStatus != 0)
					{
						if (throwOnError)
							throw new AppException($"{capitalisedName} command failed: {commandText}");
						Console.Error.WriteLine($"{capitalisedName} command failed: {commandText}");
						return false;
					}
				}
				catch (Exception ex)
				{
					if (throwOnError)
						throw new AppException($"Error executing {name} command: {commandText}\n  {ex.Message}", ex);
					Console.Error.WriteLine($"Error executing {name} command: {commandText}\n  {ex.Message}", ex);
					return false;
				}
			}
			return true;
		}

		#endregion Command execution methods
	}

	#region Configuration file data structures

	public class ConfigFile
	{
		public Dictionary<string, Profile> Profiles { get; set; }
	}

	public class Profile
	{
		[JsonConditional]
		public bool IsDefault { get; set; }
		public string HostName { get; set; }
		[JsonConditional]
		public int Port { get; set; }
		public string UserName { get; set; }
		[JsonConditional]
		public string Password { get; set; }
		[JsonConditional]
		public string KeyFileName { get; set; }
		[JsonConditional]
		public string KeyFilePassphrase { get; set; }
		public string LocalPath { get; set; }
		public string RemotePath { get; set; }
		[JsonConditional]
		public List<string> IgnoredLocalFiles { get; set; }
		[JsonConditional]
		public List<string> IgnoredRemoteFiles { get; set; }
		[JsonConditional]
		public ProfileCommands Commands { get; set; }
	}

	public class ProfileCommands
	{
		[JsonConditional]
		public List<string> PreUpload { get; set; }
		[JsonConditional]
		public List<string> PreInstall { get; set; }
		[JsonConditional]
		public List<string> PostInstall { get; set; }
	}

	#endregion Configuration file data structures

	#region File data structure

	internal class FileEntry
	{
		public string Name { get; set; }
		public DateTime UtcTime { get; set; }
		public long Length { get; set; }
	}

	#endregion File data structure

	#region Application error handling

	internal class AppException : ApplicationException
	{
		public AppException(string message)
			: base(message)
		{
		}

		public AppException(string message, Exception innerException)
			: base(message, innerException)
		{
		}
	}

	#endregion Application error handling
}
