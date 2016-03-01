using Mono.Cecil;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ApiPeek.Service
{
    internal class AssemblyLoader
    {
        private string basePath;

        public AssemblyLoader(string path)
        {
            this.basePath = path;
        }

        public virtual IEnumerable<AssemblyDefinition> GetAssemblies()
        {
            if (!Directory.Exists(basePath))
                yield break;

            foreach (var dllPath in Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories)
                .Where(p => (p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".winmd", StringComparison.OrdinalIgnoreCase))))
            {
                //if (Path.GetFileName(dllPath).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                //    Debugger.Break();//FIXME: object
                AssemblyDefinition ass = null;
                try
                {
                    ass = LoadAssembly(dllPath);
                }
                catch (BadImageFormatException)
                {
                    //eat it, see e.FusionLog for more info
                }
                catch (ArgumentException)
                {
                    //module.Assembly == null, but why?? For System.EnterpriseServices.Wrapper.dll
                    //System.EnterpriseServices.dll has 2 Modules-> System.EnterpriseServices & System.EnterpriseServices.Wrapper.dll
                }
                catch (Exception e)
                {
                    Debug.WriteLine(dllPath + "->" + e);
                }
                if (ass != null)
                    yield return ass;
            }
        }

        private AssemblyDefinition LoadAssembly(string assemblyFile)
        {
            return AssemblyDefinition.ReadAssembly(assemblyFile);
        }
    }
}
