using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using DotnetSshDeploy;

namespace SshDeploy
{
	internal class Program
	{
		private static int Main(string[] args)
		{
			AppDomain.CurrentDomain.AssemblyResolve += (s, a) =>
			{
				string assemblyName = new AssemblyName(a.Name).Name;
				if (assemblyName == "Renci.SshNet")
				{
					string resName = typeof(Program).Namespace + "." + assemblyName + ".dll.gz";
					using (var resStream = Assembly.GetEntryAssembly().GetManifestResourceStream(resName))
					using (var zipStream = new GZipStream(resStream, CompressionMode.Decompress))
					using (var memStream = new MemoryStream())
					{
						zipStream.CopyTo(memStream);
						return Assembly.Load(memStream.GetBuffer());
					}
				}
				return null;
			};

			return new Deploy().Execute(args);
		}
	}
}
