using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ApiPeek.Service
{
    [Serializable]
    internal class AssemblyLoader : IDisposable
    {
        private string basePath;
        private AppDomain appDomain;

        public AssemblyLoader(string path)
        {
            this.basePath = path;

            AppDomainSetup setup = new AppDomainSetup();
            //setup.ApplicationName = "Test";
            setup.ApplicationBase = AppDomain.CurrentDomain.BaseDirectory;// AppDomain.CurrentDomain.BaseDirectory;
            //setup.PrivateBinPath = AppDomain.CurrentDomain.BaseDirectory;//Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "private")
            setup.TargetFrameworkName = AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName;
            //setup.CachePath = setup.ApplicationBase;
            setup.ShadowCopyFiles = "false";
            //setup.ShadowCopyDirectories = setup.ApplicationBase;

            appDomain = AppDomain.CreateDomain("TempDomain", null, setup);
            appDomain.AssemblyResolve += ResolveEventHandler;// (s, e) => ResolveEventHandler(s, e);
            appDomain.ReflectionOnlyAssemblyResolve += ResolveEventHandler;//(s, e) => ResolveEventHandler(s, e);
            appDomain.TypeResolve += ResolveEventHandler;//(s, e) => ResolveEventHandler(s, e);

            string name = Assembly.GetExecutingAssembly().GetName().FullName;

            remoteLoader = (RemoteLoader)appDomain.CreateInstanceAndUnwrap(
                name,
                typeof(RemoteLoader).FullName);
        }

#if false
        private static Assembly ResolveEventHandler(Object sender, ResolveEventArgs args)
        {
            string dllName = new AssemblyName(args.Name).Name + ".dll";
            var assem = Assembly.GetExecutingAssembly();
            string resourceName = assem.GetManifestResourceNames()
                .FirstOrDefault(rn => rn.EndsWith(dllName));
            if (resourceName == null) return null; // Not found, maybe another handler will find it
            using (var stream = assem.GetManifestResourceStream(resourceName))
            {
                byte[] assemblyData = new byte[stream.Length];
                stream.Read(assemblyData, 0, assemblyData.Length);
                return Assembly.Load(assemblyData);
            }
        }
#else

        private string Search(string basePath, string name)
        {
            string dllName = name + ".dll";
            string path = Path.Combine(basePath, dllName);
            if (File.Exists(path))
                return path;

            dllName = name + ".exe";
            path = Path.Combine(basePath, dllName);
            if (File.Exists(path))
                return path;

            return null;
        }

        private Assembly ResolveEventHandler(Object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            //CodeBase: "file:///C:/Program Files (x86)/Reference Assemblies/Microsoft/Framework/.NETFramework/v3.5/Profile/Client/Microsoft.JScript.dll"
            //EscapedCodeBase: "file:///C:/Program%20Files%20(x86)/Reference%20Assemblies/Microsoft/Framework/.NETFramework/v3.5/Profile/Client/Microsoft.JScript.dll"
            //FullName: "Microsoft.JScript, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
            //Location: "C:\\Program Files (x86)\\Reference Assemblies\\Microsoft\\Framework\\.NETFramework\\v3.5\\Profile\\Client\\Microsoft.JScript.dll"
            //ImageRuntimeVersion: "v2.0.50727"
            string path =
                Search(Path.GetDirectoryName(args.RequestingAssembly.Location), name) ??//Path.GetFullPath(args.RequestingAssembly.Location + "/..")
                Search(((AppDomain)sender).BaseDirectory, name);
            try
            {
                if (path != null)
                {
                    return LoadAssembly(path);
                }
                else
                {
                    return LoadAssemblyFromGAC(args.Name);
                }
            }
            catch
            {
                return null;
            }
        }

#endif

        public void RunAtRemote(Action<object> a, object args)
        {
            remoteLoader.ExecuteMothod(a, args);
        }

        public virtual IEnumerable<Assembly> GetAssemblies()
        {
            foreach (var dllPath in Directory.EnumerateFiles(basePath, "*.*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                if (Path.GetFileName(dllPath).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase))
                    Debugger.Break();//FIXME: object
                Assembly ass = null;
                try
                {
                    ass = LoadAssembly(dllPath);
                    //ass = appDomain.Load(dllPath);
                }
                catch (BadImageFormatException)
                {
                    //eat it, see e.FusionLog for more info
                }
                catch (Exception e)
                {
                    Debug.WriteLine(dllPath + "->" + e);
                }
                yield return ass;
            }
            //foreach (var ass in appDomain.GetAssemblies())
            //{
            //    yield return ass;
            //}
            //foreach (var ass in appDomain.ReflectionOnlyGetAssemblies())
            //{
            //    yield return ass;
            //}
        }

        private Assembly LoadAssembly(string assemblyFile)
        {
            Assembly _assembly;
            try
            {
                ////Same as LoadFrom, but can not be executed? --We need some info of Enum, which need execute.
                _assembly = Assembly.ReflectionOnlyLoadFrom(assemblyFile);
                //NOTE: LoadFrom/LoadFile does not work well with strong name assembly and GAC
            }
            catch (FileLoadException)
            {
                //System.IO.FileLoadException: API 限制: 程序集“file:///C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll”已从其他位置加载。无法从同一个 Appdomain 中的另一新位置加载该程序集。
                //Normal
                _assembly = Assembly.LoadFrom(assemblyFile);
                //Not load dependence, but can load many version of same assembly?
                //_assembly = Assembly.LoadFile(assemblyFile);
            }
            return _assembly;
        }

        private Assembly LoadAssemblyFromGAC(string assemblyString)
        {
            Assembly _assembly;
            try
            {
                ////Same as LoadFrom, but can not be executed? --We need some info of Enum, which need execute.
                _assembly = Assembly.ReflectionOnlyLoad(assemblyString);
            }
            catch (FileLoadException)
            {
                //System.IO.FileLoadException: API 限制: 程序集“file:///C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\WindowsBase.dll”已从其他位置加载。无法从同一个 Appdomain 中的另一新位置加载该程序集。
                //Normal
                _assembly = Assembly.Load(assemblyString);
            }
            return _assembly;
        }

#if false
        protected static Assembly GetAssembly<T>()
        {
            try
            {
                return typeof(T).GetTypeInfo().Assembly;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return null;
            }
        }

        protected static Assembly GetAssembly(string typeQualifiedName)
        {
            try
            {
                return Type.GetType(typeQualifiedName).GetTypeInfo().Assembly;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return null;
            }
        }

        protected static Assembly GetAssemblyByName(string assemblyName)
        {
            try
            {
                return Assembly.Load(new AssemblyName(assemblyName));
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return null;
            }
        }
#endif

        #region IDisposable Support

        private bool disposedValue = false; // 要检测冗余调用
        private RemoteLoader remoteLoader;

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: 释放托管状态(托管对象)。
                    if (appDomain != null)
                    {
                        AppDomain.Unload(appDomain);
                        appDomain = null;
                    }
                }

                // TODO: 释放未托管的资源(未托管的对象)并在以下内容中替代终结器。
                // TODO: 将大型字段设置为 null。

                disposedValue = true;
            }
        }

        // TODO: 仅当以上 Dispose(bool disposing) 拥有用于释放未托管资源的代码时才替代终结器。
        // ~AssemblyLoader() {
        //   // 请勿更改此代码。将清理代码放入以上 Dispose(bool disposing) 中。
        //   Dispose(false);
        // }

        // 添加此代码以正确实现可处置模式。
        public void Dispose()
        {
            // 请勿更改此代码。将清理代码放入以上 Dispose(bool disposing) 中。
            Dispose(true);
            // TODO: 如果在以上内容中替代了终结器，则取消注释以下行。
            // GC.SuppressFinalize(this);
        }

        #endregion IDisposable Support

        public class RemoteLoader : MarshalByRefObject
        {
            private Assembly _assembly;

            public Assembly LoadAssembly(string assemblyFile)
            {
                //Normal
                _assembly = Assembly.LoadFrom(assemblyFile);
                ////Same as LoadFrom, but can not be executed? --We need some info of Enum, which need execute.
                //_assembly = Assembly.ReflectionOnlyLoadFrom(assemblyFile);
                //Not load dependence, but can load many version of same assembly?
                //_assembly = Assembly.LoadFile(assemblyFile);
                return _assembly;
            }

            public T GetInstance<T>(string typeName) where T : class
            {
                if (_assembly == null) return null;
                var type = _assembly.GetType(typeName);
                if (type == null) return null;
                return Activator.CreateInstance(type) as T;
            }

            public void ExecuteMothod(string typeName, string methodName)
            {
                if (_assembly == null) return;
                var type = _assembly.GetType(typeName);
                var obj = Activator.CreateInstance(type);
                Expression<Action> lambda = Expression.Lambda<Action>(Expression.Call(Expression.Constant(obj), type.GetMethod(methodName)), null);
                lambda.Compile()();
            }

            public void ExecuteMothod(Action<object> action, object args)
            {
                action(args);
            }
        }
    }
}
