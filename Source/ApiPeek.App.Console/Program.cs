using ApiPeek.Service;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ApiPeek.App
{
    internal class Program
    {
        static void Main(string[] args)
        {
            //ApiPeekService.PeekAndLog(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319");
            var sw = Stopwatch.StartNew();
            ApiPeekService.PeekAndLog(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319");
            ApiPeekService.PeekAndLog(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v3.5");
            ApiPeekService.PeekAndLog(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0");
            ApiPeekService.PeekAndLog(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5");
            ApiPeekService.PeekAndLog(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5.1");
            ApiPeekService.PeekAndLog(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.5.2");
            ApiPeekService.PeekAndLog(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6");
            ApiPeekService.PeekAndLog(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.1");
            sw.Stop();
            Console.WriteLine(sw.ElapsedMilliseconds);
            Console.Read();
        }
    }
}
