using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ApiPeek.Service
{
    public sealed class ApiPeekService
    {
        internal static void PeekAndLog(string path)
        {
            try
            {
                using (AssemblyLoader loader = new AssemblyLoader(path))
                {
                    //PeekAndLogRemote(Tuple.Create(loader, path));
                    loader.RunAtRemote(PeekAndLogRemote, Tuple.Create(loader, path));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static void PeekAndLogRemote(object args)
        {
            var tp = (Tuple<AssemblyLoader, string>)args;
            var loader = tp.Item1;
            var path = tp.Item2;
            var assemblies = loader.GetAssemblies().Where(a => a != null).Distinct();

            string fileName = $"peek_{Path.GetFileName(path)}_{DateTime.Now:yyyyMMddHHmmss}.zip";

            // save it to json -> ZIP file
            var fp = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            Directory.CreateDirectory(fp);
            fp = Path.Combine(fp, fileName);
            using (Stream fileStream = File.Create(fp))
            {
                using (ZipArchive zip = new ZipArchive(fileStream, ZipArchiveMode.Create, true))
                {
                    foreach (Assembly assembly in assemblies)
                    {
                        string asmName = GetAssemblyName(assembly);
                        if (asmName.EndsWith(".resources"))
                            continue;//skip

                        ZipArchiveEntry telemetryFile = zip.CreateEntry(asmName + ".json", CompressionLevel.Optimal);
                        using (StreamWriter writer = new StreamWriter(telemetryFile.Open(), Encoding.UTF8, 32768))
                        {
                            using (JsonTextWriter jsonWriter = new JsonTextWriter(writer) { Formatting = Formatting.Indented })
                            {
                                //FIXME: that should be run at remote
                                Peek(assembly, jsonWriter);
                                //jsonWriter.Flush();
                                //jsonWriter.Close();
                            }
                        }
                    }
                }
            }
        }

        private static string GetAssemblyName(Assembly assembly)
        {
            return assembly.FullName.Split(',')[0];
        }

        private static IEnumerable<TypeInfo> GetDefinedTypes(Assembly assembly)
        {
            try
            {
                //+      [0] { "类型“System.Web.DynamicData.ControlFilterExpression”违反了继承安全性规则。派生类型必须与基类型的安全可访问性匹配或者比基类型的安全可访问性低。":"System.Web.DynamicData.ControlFilterExpression"}
                //System.Exception { System.TypeLoadException}
                //+       [1] { "类型“System.Web.DynamicData.DynamicFilterExpression”违反了继承安全性规则。派生类型必须与基类型的安全可访问性匹配或者比基类型的安全可访问性低。":"System.Web.DynamicData.DynamicFilterExpression"}
                //System.Exception { System.TypeLoadException}
                //+       [2] { "类型“System.Web.DynamicData.DynamicRouteExpression”违反了继承安全性规则。派生类型必须与基类型的安全可访问性匹配或者比基类型的安全可访问性低。":"System.Web.DynamicData.DynamicRouteExpression"}
                //System.Exception { System.TypeLoadException}

                return assembly.DefinedTypes.OrderBy(t => t.FullName).ToArray();//+		assembly	{System.Web.DynamicData, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35}	System.Reflection.Assembly {System.Reflection.RuntimeAssembly}
            }
            catch (ReflectionTypeLoadException e)
            {
                //mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089->System.Reflection.ReflectionTypeLoadException: 无法加载一个或多个请求的类型。有关更多信息，请检索 LoaderExceptions 属性。
                //"未能从程序集“mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089”中加载类型“System.Object”，因为父级不存在。"
                Debug.WriteLine(assembly.FullName + "->" + e);
                return e.Types.Where(t => t != null).Select(t => t.GetTypeInfo()).Where(ti => ti != null).OrderBy(t => t.FullName).ToArray();
            }
            catch (Exception e)
            {
                Debug.WriteLine(assembly.FullName + "->" + e);
                return new TypeInfo[0];
            }
        }

        private static Type GetPropertyType(PropertyInfo prop)
        {
            try
            {
                return prop.PropertyType;
            }
            catch (Exception e)
            {
                Debug.WriteLine(e);
                return null;
            }
        }

        #region Api gather

        internal static void Peek(Assembly assembly, JsonWriter jw)
        {
            try
            {
                jw.WriteStartObject();

                WriteString("Name", assembly.FullName, jw);
                var rawTypes = GetDefinedTypes(assembly);
                WriteTypes("Enums", t => t.IsEnum, GetEnum, rawTypes, jw);
                WriteTypes("Interfaces", t => t.IsInterface, GetInterface, rawTypes, jw);
                WriteTypes("Structs", t => t.IsValueType && !t.IsEnum, GetStruct, rawTypes, jw);
                WriteTypes("Classes", IsClass, GetClass, rawTypes, jw);
                WriteTypes("Delegates", IsDelegate, GetDelegate, rawTypes, jw);

                jw.WriteEndObject();
            }
            catch (Exception e)
            {
                //mscorlib, Version = 4.0.0.0, Culture = neutral, PublicKeyToken = b77a5c561934e089->Error in Peek: System.TypeLoadException: 未能从程序集“mscorlib, Version = 4.0.0.0, Culture = neutral, PublicKeyToken = b77a5c561934e089”中加载类型“System.Object”，因为父级不存在。
                Debug.WriteLine(assembly.FullName + "->Error in Peek: " + e);
            }
        }

        private static void WriteTypes(string name, Func<TypeInfo, bool> test, Action<TypeInfo, JsonWriter> action,
            IEnumerable<TypeInfo> rawTypes, JsonWriter jw)
        {
            //TypeInfo[] types = assembly.DefinedTypes.Where(t => t.IsPublic && test(t)).ToArray();
            TypeInfo[] types = rawTypes.Where(t => t.IsPublic && test(t)).ToArray();
            if (types.Length == 0) return;

            jw.WritePropertyName(name);
            jw.WriteStartArray();
            foreach (TypeInfo type in types)
            {
                action(type, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetEnum(TypeInfo enm, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", enm.AsType().GetFullTypeName(), jw);
            if (enm.Assembly.ReflectionOnly)
                WriteBool("IsFlags", enm.GetCustomAttributesData().Any(cad => cad.AttributeType == typeof(FlagsAttribute)), jw);
            else
                WriteBool("IsFlags", enm.IsDefined(typeof(FlagsAttribute), false), jw);
            Type basetype = Enum.GetUnderlyingType(enm.AsType());
            WriteString("BaseType", basetype.Name, jw);
            GetEnumValues(enm, basetype, jw);

            jw.WriteEndObject();
        }

        private static void GetInterface(TypeInfo iface, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", iface.AsType().GetFullTypeName(), jw);
            GetInterfaces(iface, jw);
            GetProperties(iface, jw);
            GetMethods(iface, jw);
            GetEvents(iface, jw);

            jw.WriteEndObject();
        }

        private static void GetStruct(TypeInfo strct, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", strct.AsType().GetFullTypeName(), jw);
            GetInterfaces(strct, jw);
            GetConstructors(strct, jw);
            GetProperties(strct, jw);
            GetFields(strct, jw);
            GetMethods(strct, jw);
            GetEvents(strct, jw);

            jw.WriteEndObject();
        }

        private static readonly Type objType = typeof(object);

        private static void GetClass(TypeInfo clss, JsonWriter jw)
        {
            jw.WriteStartObject();

            string name = clss.AsType().GetFullTypeName();
            WriteString("Name", name, jw);
            WriteBool("IsStatic", clss.IsAbstract && clss.IsSealed, jw);
            WriteBool("IsSealed", !clss.IsAbstract && clss.IsSealed, jw);
            WriteBool("IsAbstract", clss.IsAbstract && !clss.IsSealed, jw);
            //NOTE: typeof(object).BaseType == null
            if (clss.BaseType != objType && clss.BaseType != null && clss.BaseType.GetTypeInfo().IsPublic) WriteString("BaseType", clss.BaseType.GetTypeName(), jw);
            GetInterfaces(clss, jw);
            GetConstructors(clss, jw);
            GetProperties(clss, jw);
            GetFields(clss, jw);
            GetMethods(clss, jw);
            GetEvents(clss, jw);

            jw.WriteEndObject();
        }

        private static void GetDelegate(TypeInfo del, JsonWriter jw)
        {
            jw.WriteStartObject();

            string name = del.AsType().GetFullTypeName();
            WriteString("Name", name, jw);
            MethodInfo invoke = del.DeclaredMethods.FirstOrDefault(m => m.Name == "Invoke");
            if (invoke == null)
            {
                //NOTE: MulticastDelegate
                Debugger.Break();
                jw.WriteEndObject();
                return;
            }
            WriteString("ReturnType", invoke.ReturnType.GetTypeName(), jw);
            GetMethodParameters(invoke, jw);

            jw.WriteEndObject();
        }

        private static void GetEnumValues(TypeInfo enm, Type baseType, JsonWriter jw)
        {
            FieldInfo[] fields = enm.DeclaredFields.Where(InterestingName).OrderBy(mi => mi.Name).ToArray();
            if (fields.Length == 0) return;

            jw.WritePropertyName("Values");
            jw.WriteStartArray();
            foreach (FieldInfo field in fields)
            {
                GetEnumValue(field, baseType, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetEnumValue(FieldInfo par, Type baseType, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", par.Name, jw);
            //object fieldValue = Convert.ChangeType(par.GetValue(null), baseType);
            object fieldValue = Convert.ChangeType(par.GetRawConstantValue(), baseType);
            WriteString("Value", fieldValue.ToString(), jw);

            jw.WriteEndObject();
        }

        private static void GetInterfaces(TypeInfo type, JsonWriter jw)
        {
            Type[] interfaces = type.ImplementedInterfaces.Where(i => i.GetTypeInfo().IsPublic).OrderBy(mi => mi.Name).ToArray();
            if (interfaces.Length == 0) return;

            jw.WritePropertyName("Interfaces");
            jw.WriteStartArray();
            foreach (Type ifaceType in interfaces)
            {
                jw.WriteValue(ifaceType.GetTypeName());
            }
            jw.WriteEndArray();
        }

        private static void GetConstructors(TypeInfo type, JsonWriter jw)
        {
            ConstructorInfo[] constructors = type.DeclaredConstructors.Where(InterestingName).OrderBy(mi => mi.Name).ToArray();
            if (constructors.Length == 0) return;
            if (constructors.Length == 1)
            {
                // do not log if there is only the defualt constructor
                ParameterInfo[] parameters = constructors[0].GetParameters();
                if (parameters.IsNullOrEmpty()) return;
            }

            jw.WritePropertyName("Constructors");
            jw.WriteStartArray();
            foreach (ConstructorInfo ctor in constructors)
            {
                GetConstructor(ctor, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetConstructor(ConstructorInfo ctor, JsonWriter jw)
        {
            jw.WriteStartObject();

            GetMethodParameters(ctor, jw);

            jw.WriteEndObject();
        }

        private static void GetProperties(TypeInfo type, JsonWriter jw)
        {
            //PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            //PropertyInfo[] sproperties = type.GetProperties(BindingFlags.Public | BindingFlags.Static);
            PropertyInfo[] properties = type.DeclaredProperties.Where(InterestingName).OrderBy(mi => mi.Name).ToArray();
            if (properties.Length == 0) return;

            jw.WritePropertyName("Properties");
            jw.WriteStartArray();
            foreach (PropertyInfo prop in properties)
            {
                GetProperty(prop, PropertyIsStatic(prop), jw);
            }
            jw.WriteEndArray();
        }

        private static void GetProperty(PropertyInfo prop, bool isStatic, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", prop.Name, jw);
            WriteString("Type", GetPropertyType(prop).GetTypeName(), jw);
            WriteBool("IsStatic", isStatic, jw);
            WriteBool("IsGet", prop.CanRead, jw);
            WriteBool("IsSet", prop.CanWrite, jw);

            jw.WriteEndObject();
        }

        private static void GetFields(TypeInfo type, JsonWriter jw)
        {
            //FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(InterestingName).ToArray();
            //FieldInfo[] sfields = type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(InterestingName).ToArray();
            FieldInfo[] fields = type.DeclaredFields.Where(InterestingName).OrderBy(mi => mi.Name).ToArray();
            if (fields.Length == 0) return;

            jw.WritePropertyName("Fields");
            jw.WriteStartArray();
            foreach (FieldInfo field in fields)
            {
                GetField(field, field.IsStatic, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetField(FieldInfo field, bool isStatic, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", field.Name, jw);
            WriteString("Type", field.FieldType.GetTypeName(), jw);
            WriteBool("IsStatic", isStatic, jw);

            jw.WriteEndObject();
        }

        private static void GetMethods(TypeInfo type, JsonWriter jw)
        {
            //MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(InterestingName).ToArray();
            //MethodInfo[] smethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(InterestingName).ToArray();
            MethodInfo[] methods = type.DeclaredMethods.Where(InterestingName).OrderBy(mi => mi.Name).ToArray();
            if (methods.Length == 0) return;

            jw.WritePropertyName("Methods");
            jw.WriteStartArray();
            foreach (MethodInfo method in methods)
            {
                GetMethod(method, method.IsStatic, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetMethod(MethodInfo meth, bool isStatic, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", meth.Name, jw);
            WriteBool("IsStatic", isStatic, jw);
            WriteString("ReturnType", meth.ReturnType.GetTypeName(), jw);
            GetMethodParameters(meth, jw);

            jw.WriteEndObject();
        }

        private static void GetMethodParameters(MethodBase method, JsonWriter jw)
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0) return;

            jw.WritePropertyName("Parameters");
            jw.WriteStartArray();
            foreach (ParameterInfo par in parameters)
            {
                GetMethodParameter(par, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetMethodParameter(ParameterInfo par, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", par.Name, jw);
            WriteString("Type", par.ParameterType.GetTypeName(), jw);
            WriteBool("Out", par.IsOut, jw);

            jw.WriteEndObject();
        }

        private static void GetEvents(TypeInfo type, JsonWriter jw)
        {
            //EventInfo[] events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance).ToArray();
            //EventInfo[] sevents = type.GetEvents(BindingFlags.Public | BindingFlags.Static).ToArray();
            //TODO: InterestingName??
            EventInfo[] events = type.DeclaredEvents.OrderBy(mi => mi.Name).ToArray();
            if (events.Length == 0) return;

            jw.WritePropertyName("Events");
            jw.WriteStartArray();
            foreach (EventInfo evnt in events)
            {
                GetEvent(evnt, EventIsStatic(evnt), jw);
            }
            jw.WriteEndArray();
        }

        private static void GetEvent(EventInfo evnt, bool isStatic, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", evnt.Name, jw);
            WriteString("Type", evnt.EventHandlerType.GetTypeName(), jw);
            WriteBool("IsStatic", isStatic, jw);

            jw.WriteEndObject();
        }

        private static void WriteString(string name, string stringValue, JsonWriter jw)
        {
            if (string.IsNullOrWhiteSpace(stringValue)) return;
            jw.WritePropertyName(name);
            jw.WriteValue(stringValue);
        }

        private static void WriteBool(string name, bool boolValue, JsonWriter jw)
        {
            if (!boolValue) return;
            jw.WritePropertyName(name);
            jw.WriteValue(true);
        }

        private static readonly TypeInfo delegateTI = typeof(MulticastDelegate).GetTypeInfo();

        private static bool IsDelegate(TypeInfo ti)
        {
            return delegateTI.IsAssignableFrom(ti) && ti != delegateTI;
        }

        private static bool IsClass(TypeInfo ti)
        {
            return ti.IsClass && !IsDelegate(ti);
        }

        private static bool PropertyIsStatic(PropertyInfo p)
        {
            return (p.GetMethod != null && p.GetMethod.IsStatic) || (p.SetMethod != null && p.SetMethod.IsStatic);
        }

        private static bool EventIsStatic(EventInfo e)
        {
            return (e.AddMethod != null && e.AddMethod.IsStatic) || (e.RemoveMethod != null && e.RemoveMethod.IsStatic);
        }

        private static readonly ISet<string> ignoredMethods = new HashSet<string>() { "GetHashCode", "ToString", "Equals", "GetType", "CompareTo", "HasFlag", "GetTypeCode" };

        private static bool InterestingName(MethodInfo mi)
        {
            return mi.IsPublic && !mi.IsSpecialName && !ignoredMethods.Contains(mi.Name);
        }

        private static readonly ISet<string> ignoredProperties = new HashSet<string>() { };

        private static bool InterestingName(PropertyInfo pi)
        {
            return !pi.IsSpecialName && !ignoredProperties.Contains(pi.Name);
        }

        private static readonly ISet<string> ignoredFields = new HashSet<string>() { "value__", };

        private static bool InterestingName(FieldInfo fi)
        {
            return fi.IsPublic && !fi.IsSpecialName && !ignoredFields.Contains(fi.Name);
        }

        private static readonly ISet<string> ignoredConstructors = new HashSet<string>() { };

        private static bool InterestingName(ConstructorInfo fi)
        {
            return fi.IsPublic && !ignoredConstructors.Contains(fi.Name);
        }

        #endregion Api gather
    }

    internal static class StringExt
    {
        public static bool IsNullOrEmpty<TSource>(this ICollection<TSource> collection)
        {
            return collection == null || !collection.Any();
        }

        public static string GetTypeName(this Type t)
        {
            if (t == null) return "unknown";
            TypeInfo ti = t.GetTypeInfo();
            if (!ti.IsGenericType) return t.Name;

            string genTypeName = t.GetGenericTypeDefinition().Name;
            if (genTypeName.Contains("`")) genTypeName = genTypeName.Substring(0, genTypeName.IndexOf('`'));
            string genArgs = string.Join(",", ti.GenericTypeParameters.Select(GetTypeName));
            if (genArgs == "") genArgs = string.Join(",", ti.GenericTypeArguments.Select(GetTypeName));
            if (genArgs == "") Debugger.Break();
            return genTypeName + "<" + genArgs + ">";
        }

        public static string GetFullTypeName(this Type t)
        {
            if (t == null) return "unknown";
            TypeInfo ti = t.GetTypeInfo();
            if (!ti.IsGenericType) return t.FullName;

            string genTypeName = t.GetGenericTypeDefinition().FullName;
            if (genTypeName.Contains("`")) genTypeName = genTypeName.Substring(0, genTypeName.IndexOf('`'));
            string genArgs = string.Join(",", ti.GenericTypeParameters.Select(GetTypeName));
            if (genArgs == "") genArgs = string.Join(",", ti.GenericTypeArguments.Select(GetTypeName));
            if (genArgs == "") Debugger.Break();
            return genTypeName + "<" + genArgs + ">";
        }
    }
}
