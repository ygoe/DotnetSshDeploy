# dotnet-ssh-deploy

Deploys websites and applications to SSH servers through the dotnet CLI command or as standalone command-line tool.

[![NuGet](https://img.shields.io/nuget/v/Unclassified.DotnetSshDeploy.svg)](https://www.nuget.org/packages/Unclassified.DotnetSshDeploy)

## Features

* Uploads new or modified files to a temporary directory
* Copies uploaded files directly on the server to minimise downtime or half-uploaded file confusion
* Runs commands before and after installation to stop and start the application server
* Can ignore local and remote files by simple glob patterns (`?`, `*` and `**` only)
* Supports multiple deployment profiles
* Multi-threaded remote file operations, much faster over internet connections

## Installation

### dotnet local tool

Install the NuGet package **Unclassified.DotnetSshDeploy** to your project directory. Then you can run it from the project directory to deploy your web application. This requires the [.NET 5.0 runtime](https://dotnet.microsoft.com/download) to be installed.

Installation:

    dotnet new tool-manifest
    dotnet tool install Unclassified.DotnetSshDeploy

Command invocation:

    dotnet publish -c Release
    dotnet ssh-deploy

If you have multiple target environments to deploy to, you can select one of the configured profiles:

    dotnet ssh-deploy staging

### dotnet global tool

Install the NuGet package **Unclassified.DotnetSshDeploy** as a global tool. Then you can run it from all directories to deploy your web application. This requires the [.NET 5.0 runtime](https://dotnet.microsoft.com/download) to be installed.

Installation:

    dotnet tool install -g Unclassified.DotnetSshDeploy

Command invocation:

    ssh-deploy

[Learn more about managing .NET tools.](https://docs.microsoft.com/en-us/dotnet/core/tools/global-tools)

### standalone

To use this tool in other environments than dotnet projects, use the separate standalone console application. It’s a single executable that depends on the .NET Framework 4.6.1 or later. You can place this program file somewhere in your %PATH% so you can quickly run it from all your web projects. But you can simply save it in your project directory as well. It is invoked similarly and accepts all the same command line options:

    ssh-deploy

You will also need a profile configuration file which is described below.

## Configuration

### sshDeploy.json

The deployment is configured through a single file that should be placed somewhere in your project directory. This file contains all deployment profiles.

> ⚠️ **Security notice:**
>
> You don’t want hackers to deploy anything to your site, do you? Because the config file contains access credentials or at least personal data, **it should never be added to version control**. Add it to your **.gitignore** file. It should also never land on your web server (it has no use there anyway). Keep this file local and give it to everybody who needs to deploy the project to your server. You might check in a template of this file that contains all public data and has placeholders for confidential data, if you like.

The file sshDeploy.json is automatically discovered in the current working directory and in its subdirectory “Properties” which is common for .NET/Visual Studio projects. If it’s located somewhere else, specify its path with the `-c` command line option.

The JSON structure looks like this:

```json
{
  "profiles": {
    "profilename": {
      "isDefault": true,
      "hostName": "example.com",
      "userName": "appuser",
      "keyFileName": "sshDeploy.key",
      "keyFilePassphrase": "keypassphrase",
      "localPath": "../bin/Release/netcoreapp3.1/publish",
      "remotePath": "app",
      "ignoredLocalFiles": [
        "appsettings.Development.json"
      ],
      "ignoredRemoteFiles": [
        "cgi-bin/**"
      ],
      "commands": {
        "preInstall": [
          "aspnetappctl stop"
        ],
        "postInstall": [
          "aspnetappctl start"
        ]
      }
    }
  }
}
```

#### `profiles`

The `profiles` property contains a map of all profile names with their data. In this example, the profile name is “profilename”. Call it “production”, “staging” or whatever you want. You can add multiple of these profiles. One of them can be the default profile.

#### `isDefault` <small>*(optional)*</small>

Declares the default profile that is used when none is specified on the command line. If this option is not specified and there is no default profile, the first profile is used if there is only one.

#### `hostName`, `port` <small>*(optional, not shown)*</small>, `userName`

SSH connection and login data. The port defaults to 22.

#### `password` <small>*(optional)*</small>

The plain password for SSH login.

#### `keyFileName` <small>*(optional)*</small>, `keyFilePassphrase` <small>*(optional)*</small>

The name of the key file and, if required, its passphrase for SSH login. Key files must be in PEM format. PuTTY or OPENSSH files are not supported. The file name can contain environment variables in Windows syntax like “%userprofile%/deploy.key”. Non-rooted file names are evaluated relative to the config file directory. Directory separators can be backslash (`\`) or forward slash (`/`), but the forward slash is more convenient in JSON files.

Either a password or a key file must be specified.

#### `localPath`

The path to the local files to be deployed. This is usually the output directory of `dotnet publish` or just your project directory for other web project types. It can contain environment variables in Windows syntax like “%myprojects%/projectname”. Non-rooted paths are evaluated relative to the config file directory. Directory separators can be backslash (`\`) or forward slash (`/`), but the forward slash is more convenient in JSON files.

#### `remotePath`

The path on the remote server to deploy the files into. This path is relative to the home directory that you are in after SSH login.

#### `ignoredLocalFiles` <small>*(optional)*</small>

A list of names or glob patterns of local files that are not deployed.

#### `ignoredRemotefiles` <small>*(optional)*</small>

A list of names or glob patterns of remote files that are not deleted. If ssh-deploy finds a remote file that doesn’t exist locally and is not matched by this list, it asks you what to do with it. If you choose to always keep it, it is added to this list.

#### `commands` <small>*(optional)*</small>

Commands can be executed on the SSH server at different events:

#### `commands.preUpload` <small>*(optional, not shown)*</small>

A list of commands to execute on the SSH server before files are uploaded.

#### `commands.preInstall` <small>*(optional)*</small>

A list of commands to execute on the SSH server after the files are uploaded and before files are deleted and copied from the temporary upload directory. Use this to stop the web application server.

#### `commands.postInstall` <small>*(optional)*</small>

A list of commands to execute on the SSH server after the files are deleted and copied. Use this to start the web application server again.

### Command line options

    ssh-deploy [-c configfile] [-e] [-p] [-q] [-s] [-v] [profilename]

#### `-c`

Specifies the configuration file to use. See above for default locations if unspecified.

#### `-e`

Asks for the SSH login password and stores it in encrypted form into the profile. (This is only supported on Windows with the standalone tool.)

#### `-p`

Hide upload progress. Prints normal messages but no upload progress information. Quiet mode includes this.

#### `-q`

Quiet mode. Does not print anything except error messages and questions about what to do with remote-only files.

#### `-s`

Single-thread mode. Performs all remote file operations in the main thread and does not scan/upload/delete files in parallel. This takes a lot longer over the internet but might be helpful with parallel errors when uploading/deleting files.

#### `-v`

Verbose mode. Prints more details about what is going on. Useful for troubleshooting.

If no profile name is specified, the default or single profile is used. See the `isDefault` property above.

Currently there is no **non-interactive** mode. ssh-deploy will always ask you when it finds an unknown remote file. If you cannot answer because the process runs somewhere hidden, it will be stuck.

## Building

You can build this solution in Visual Studio or by running the command:

    build.cmd

### Requirements

Visual Studio 2019 or later with .NET 5.0 support is required to build this solution.

The standalone project embeds a gzip-compressed copy of the SSH.NET DLL file. This file is created before building by calling `7za`, a standalone console version of the popular 7-Zip compression application. So make sure that `7za` is available in your %PATH%. You can [download it](https://7-zip.org/download.html) with the “7-Zip Extra” archive. This compression is performed by the prebuild.cmd file.

### Troubleshooting

If the build fails with an error message like “invalid parameter”, make sure there are not multiple SSH.NET directories in the packages directory. Only the currently used version must exist there. 7za cannot guess which version to pack.

## License

[MIT license](https://github.com/ygoe/DotnetSshDeploy/blob/master/LICENSE)
