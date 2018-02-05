using System;

namespace DotnetSshDeploy
{
	class Program
	{
		static int Main(string[] args)
		{
			return new Deploy().Execute(args);
		}
	}
}
