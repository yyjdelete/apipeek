using Mono.Cecil;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
                AssemblyLoader loader = new AssemblyLoader(path);
                {
                    PeekAndLogRemote(Tuple.Create(loader, path));
                    //loader.RunAtRemote(PeekAndLogRemote, Tuple.Create(loader, path));
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
            if (!Directory.Exists(path))
                return;
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
                    foreach (var assembly in assemblies)
                    {
                        if (assembly.IsResources() || assembly.IsNativeImage())
                            continue;

                        string asmName = GetAssemblyName(assembly);
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

        private static string GetAssemblyName(AssemblyDefinition assembly)
        {
            return assembly.Name.Name;//.FullName.Split(',')[0]
        }

        private static IEnumerable<TypeDefinition> GetDefinedTypes(AssemblyDefinition assembly)
        {
            //FIXME:MainModule/Modules, Types/ExportedTypes
            //[TypeDefinition]GetTypes包括Nest的, Types不含
            //[ExportedType]ExportedTypes ??和[TypeReference]GetTypeReferences 引用的其他库中的类型
            return assembly.MainModule.GetTypes().Where(t => t.IsPublic).OrderBy(t => t.FullName).ToArray();
        }

        private static TypeReference GetPropertyType(PropertyDefinition prop)
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

        internal static void Peek(AssemblyDefinition assembly, JsonWriter jw)
        {
            try
            {
                //Debug.Assert(assembly.Modules.Count == 1);
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

        private static void WriteTypes(string name, Func<TypeDefinition, bool> test, Action<TypeDefinition, JsonWriter> action,
            IEnumerable<TypeDefinition> rawTypes, JsonWriter jw)
        {
            //TypeDefinition[] types = assembly.DefinedTypes.Where(t => t.IsPublic && test(t)).ToArray();
            TypeDefinition[] types = rawTypes.Where(t => test(t)).ToArray();
            if (types.Length == 0) return;

            jw.WritePropertyName(name);
            jw.WriteStartArray();
            foreach (TypeDefinition type in types)
            {
                action(type, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetEnum(TypeDefinition enm, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", enm.FullName, jw);
            WriteBool("IsFlags", enm.CustomAttributes.Any(ca =>
            ca.AttributeType.IsTypeOf(typeof(FlagsAttribute))), jw);
            TypeReference basetype = enm.GetEnumUnderlyingType();
            WriteString("BaseType", basetype.Name, jw);
            GetEnumValues(enm, basetype, jw);

            jw.WriteEndObject();
        }

        private static void GetInterface(TypeDefinition iface, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", iface.GetFullTypeName(), jw);
            GetInterfaces(iface, jw);
            GetProperties(iface, jw);
            GetMethods(iface, jw);
            GetEvents(iface, jw);

            jw.WriteEndObject();
        }

        private static void GetStruct(TypeDefinition strct, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", strct.GetFullTypeName(), jw);
            GetInterfaces(strct, jw);
            GetConstructors(strct, jw);
            GetProperties(strct, jw);
            GetFields(strct, jw);
            GetMethods(strct, jw);
            GetEvents(strct, jw);

            jw.WriteEndObject();
        }

        private static void GetClass(TypeDefinition clss, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", clss.GetFullTypeName(), jw);
            WriteBool("IsStatic", clss.IsAbstract && clss.IsSealed, jw);
            WriteBool("IsSealed", !clss.IsAbstract && clss.IsSealed, jw);
            WriteBool("IsAbstract", clss.IsAbstract && !clss.IsSealed, jw);
            //NOTE: typeof(object).BaseType == null
            //NOTE: subclass's visibity can not be higher than base
            if (clss.BaseType != null && !clss.BaseType.IsObject())
                WriteString("BaseType", clss.BaseType.GetTypeName(), jw);
            GetInterfaces(clss, jw);
            GetConstructors(clss, jw);
            GetProperties(clss, jw);
            GetFields(clss, jw);
            GetMethods(clss, jw);
            GetEvents(clss, jw);

            jw.WriteEndObject();
        }

        private static void GetDelegate(TypeDefinition del, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", del.GetFullTypeName(), jw);
            MethodDefinition invoke = del.GetMethods().FirstOrDefault(m => m.Name == "Invoke");
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

        private static void GetEnumValues(TypeDefinition enm, TypeReference baseType, JsonWriter jw)
        {
            FieldDefinition[] fields = enm.Fields.Where(InterestingName4Fields).OrderBy(mi => mi.Name).ToArray();
            if (fields.Length == 0) return;

            jw.WritePropertyName("Values");
            jw.WriteStartArray();
            foreach (FieldDefinition field in fields)
            {
                GetEnumValue(field, baseType, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetEnumValue(FieldDefinition par, TypeReference baseType, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", par.Name, jw);
            object fieldValue = par.Constant;
            WriteString("Value", fieldValue.ToString(), jw);

            jw.WriteEndObject();
        }

        private static void GetInterfaces(TypeDefinition type, JsonWriter jw)
        {
            //NOTE: subclass's visibity can not be higher than base
            var interfaces = type.Interfaces.OrderBy(mi => mi.Name).ToArray();
            if (interfaces.Length == 0) return;

            jw.WritePropertyName("Interfaces");
            jw.WriteStartArray();
            foreach (TypeReference ifaceType in interfaces)
            {
                jw.WriteValue(ifaceType.GetTypeName());
            }
            jw.WriteEndArray();
        }

        private static void GetConstructors(TypeDefinition type, JsonWriter jw)
        {
            var constructors = type.GetConstructors().Where(InterestingName4Constructors).OrderBy(mi => mi.Name).ToArray();
            if (constructors.Length == 0) return;
            if (constructors.Length == 1)
            {
                // do not log if there is only the defualt constructor
                var parameters = constructors[0].Parameters;
                if (parameters.IsNullOrEmpty()) return;
            }

            jw.WritePropertyName("Constructors");
            jw.WriteStartArray();
            foreach (var ctor in constructors)
            {
                GetConstructor(ctor, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetConstructor(MethodDefinition ctor, JsonWriter jw)
        {
            jw.WriteStartObject();

            GetMethodParameters(ctor, jw);

            jw.WriteEndObject();
        }

        private static void GetProperties(TypeDefinition type, JsonWriter jw)
        {
            //PropertyDefinition[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            //PropertyDefinition[] sproperties = type.GetProperties(BindingFlags.Public | BindingFlags.Static);
            PropertyDefinition[] properties = type.Properties.Where(InterestingName4Properties).OrderBy(mi => mi.Name).ToArray();
            if (properties.Length == 0) return;

            jw.WritePropertyName("Properties");
            jw.WriteStartArray();
            foreach (PropertyDefinition prop in properties)
            {
                GetProperty(prop, PropertyIsStatic(prop), jw);
            }
            jw.WriteEndArray();
        }

        private static void GetProperty(PropertyDefinition prop, bool isStatic, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", prop.Name, jw);
            WriteString("Type", GetPropertyType(prop).GetTypeName(), jw);
            WriteBool("IsStatic", isStatic, jw);
            WriteBool("IsGet", prop.GetMethod != null, jw);
            WriteBool("IsSet", prop.SetMethod != null, jw);

            jw.WriteEndObject();
        }

        private static void GetFields(TypeDefinition type, JsonWriter jw)
        {
            //FieldDefinition[] fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(InterestingName).ToArray();
            //FieldDefinition[] sfields = type.GetFields(BindingFlags.Public | BindingFlags.Static).Where(InterestingName).ToArray();
            FieldDefinition[] fields = type.Fields.Where(InterestingName4Fields).OrderBy(mi => mi.Name).ToArray();
            if (fields.Length == 0) return;

            jw.WritePropertyName("Fields");
            jw.WriteStartArray();
            foreach (FieldDefinition field in fields)
            {
                GetField(field, field.IsStatic, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetField(FieldDefinition field, bool isStatic, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", field.Name, jw);
            WriteString("Type", field.FieldType.GetTypeName(), jw);
            WriteBool("IsStatic", isStatic, jw);

            jw.WriteEndObject();
        }

        private static void GetMethods(TypeDefinition type, JsonWriter jw)
        {
            //MethodDefinition[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(InterestingName).ToArray();
            //MethodDefinition[] smethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(InterestingName).ToArray();
            MethodDefinition[] methods = type.GetMethods().Where(InterestingName4Methods).OrderBy(mi => mi.Name).ToArray();
            if (methods.Length == 0) return;

            jw.WritePropertyName("Methods");
            jw.WriteStartArray();
            foreach (MethodDefinition method in methods)
            {
                GetMethod(method, method.IsStatic, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetMethod(MethodDefinition meth, bool isStatic, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", meth.Name, jw);
            WriteBool("IsStatic", isStatic, jw);
            WriteString("ReturnType", meth.ReturnType.GetTypeName(), jw);
            GetMethodParameters(meth, jw);

            jw.WriteEndObject();
        }

        private static void GetMethodParameters(MethodDefinition method, JsonWriter jw)
        {
            var parameters = method.Parameters;
            if (parameters.Count == 0) return;

            jw.WritePropertyName("Parameters");
            jw.WriteStartArray();
            foreach (ParameterDefinition par in parameters)
            {
                GetMethodParameter(par, jw);
            }
            jw.WriteEndArray();
        }

        private static void GetMethodParameter(ParameterDefinition par, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", par.Name, jw);
            WriteString("Type", par.ParameterType.GetTypeName(), jw);
            WriteBool("Out", par.IsOut, jw);

            jw.WriteEndObject();
        }

        private static void GetEvents(TypeDefinition type, JsonWriter jw)
        {
            //EventDefinition[] events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance).ToArray();
            //EventDefinition[] sevents = type.GetEvents(BindingFlags.Public | BindingFlags.Static).ToArray();
            //TODO: InterestingName??
            EventDefinition[] events = type.Events.OrderBy(mi => mi.Name).ToArray();
            if (events.Length == 0) return;

            jw.WritePropertyName("Events");
            jw.WriteStartArray();
            foreach (EventDefinition evnt in events)
            {
                GetEvent(evnt, EventIsStatic(evnt), jw);
            }
            jw.WriteEndArray();
        }

        private static void GetEvent(EventDefinition evnt, bool isStatic, JsonWriter jw)
        {
            jw.WriteStartObject();

            WriteString("Name", evnt.Name, jw);
            WriteString("Type", evnt.EventType.GetTypeName(), jw);
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

        private static bool IsDelegate(TypeDefinition ti)
        {
            return ti.IsDelegate();
        }

        private static bool IsClass(TypeDefinition ti)
        {
            //NOTE: Cecil treat all Enum/ValueType as class
            return ti.IsClass && !ti.IsValueType/* && !ti.IsEnum*/ && !IsDelegate(ti);
        }

        private static bool PropertyIsStatic(PropertyDefinition p)
        {
            return (p.GetMethod != null && p.GetMethod.IsStatic) || (p.SetMethod != null && p.SetMethod.IsStatic);
        }

        private static bool EventIsStatic(EventDefinition e)
        {
            return (e.AddMethod != null && e.AddMethod.IsStatic) || (e.RemoveMethod != null && e.RemoveMethod.IsStatic);
        }

        private static readonly ISet<string> ignoredMethods = new HashSet<string>() { "GetHashCode", "ToString", "Equals", "GetType", "CompareTo", "HasFlag", "GetTypeCode" };

        private static bool InterestingName4Methods(MethodDefinition mi)
        {
            return mi.IsPublic && !mi.IsSpecialName && !ignoredMethods.Contains(mi.Name);
        }

        private static readonly ISet<string> ignoredProperties = new HashSet<string>() { };

        private static bool InterestingName4Properties(PropertyDefinition pi)
        {
            return !pi.IsSpecialName && !ignoredProperties.Contains(pi.Name);
        }

        private static readonly ISet<string> ignoredFields = new HashSet<string>() { "value__", };

        private static bool InterestingName4Fields(FieldDefinition fi)
        {
            return fi.IsPublic && !fi.IsSpecialName && !ignoredFields.Contains(fi.Name);
        }

        private static readonly ISet<string> ignoredConstructors = new HashSet<string>() { };

        private static bool InterestingName4Constructors(MethodDefinition fi)
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
    }

    public static class CecilHelper
    {
        public static bool IsTypeOf(this TypeReference self, string @namespace, string name)
        {
            return self.Name == name
                && self.Namespace == @namespace;
        }

        public static bool IsObject(this TypeReference self)
        {
            return self.MetadataType == MetadataType.Object;
        }

        public static bool IsTypeOf(this TypeReference self, Type other)
        {
            //NOTE: Resolve()之前, .Module 是引用程序集而不是被引用程序集
            if (self.Name == other.Name &&
                self.Namespace == other.Namespace)
            {
                //self.Scope.Name == other.Module.Name &&
                //self.Scope.Name == other.Module.Assembly.GetName().Name;
                switch (self.Scope.MetadataScopeType)
                {
                    case MetadataScopeType.AssemblyNameReference:
                        //TypeDefinition
                        return ((AssemblyNameReference)self.Scope).Name == other.Assembly.GetName().Name;

                    case MetadataScopeType.ModuleDefinition:
                        //TypeReference
                        return ((ModuleDefinition)self.Scope).Name == other.Module.ScopeName;

                    default:
                    case MetadataScopeType.ModuleReference:
                        throw new ArgumentException();
                }
            }
            return false;
        }

        public static bool IsDelegate(this TypeDefinition self)
        {
            //Delegate的其他子类呢
            return self.IsAutoClass && self.BaseType != null && self.IsTypeOf(typeof(MulticastDelegate));
        }

        public static TypeReference GetEnumUnderlyingType(this TypeDefinition self)
        {
            return Mono.Cecil.Rocks.TypeDefinitionRocks.GetEnumUnderlyingType(self);
        }

        public static IEnumerable<MethodDefinition> GetMethods(this TypeDefinition self)
        {
            return Mono.Cecil.Rocks.TypeDefinitionRocks.GetMethods(self);
        }

        public static IEnumerable<MethodDefinition> GetConstructors(this TypeDefinition self)
        {
            return Mono.Cecil.Rocks.TypeDefinitionRocks.GetConstructors(self);
        }

        public static string GetTypeName(this TypeReference t)
        {
            if (t == null) return "unknown";
            if (!t.HasGenericParameters) return t.Name;

            var name = new StringBuilder();
            var genTypeName = t.Name;
            if (genTypeName.Contains("`")) genTypeName = genTypeName.Substring(0, genTypeName.IndexOf('`'));
            name.Append(genTypeName);
            t.GenericInstanceName(name);
            return name.ToString();
        }

        public static string GetFullTypeName(this TypeReference t)
        {
            if (t == null) return "unknown";
            if (!t.HasGenericParameters) return t.FullName;

            //see GenericInstanceType
            var name = new StringBuilder();
            var genTypeName = t.FullName;//FIXME:
            if (genTypeName.Contains("`")) genTypeName = genTypeName.Substring(0, genTypeName.IndexOf('`'));
            name.Append(genTypeName);
            t.GenericInstanceFullName(name);
            return name.ToString();
            //return t.FullName;
        }

        public static void GenericInstanceName(this TypeReference self, StringBuilder builder)
        {
            var gi = self as IGenericInstance;
            if (gi != null)
            {
                builder.Append("<");
                var arguments = gi.GenericArguments;
                Debug.Assert(arguments.Count > 0);
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (i > 0)
                        builder.Append(",");
                    builder.Append(arguments[i].GetTypeName());
                }
                builder.Append(">");
                return;
            }
            var gpp = self as IGenericParameterProvider;
            if (gpp != null && gpp.HasGenericParameters)
            {
                builder.Append("<");
                var arguments = gpp.GenericParameters;
                Debug.Assert(arguments.Count > 0);
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (i > 0)
                        builder.Append(",");
                    builder.Append(arguments[i].GetTypeName());
                }
                builder.Append(">");
                return;
            }
        }

        public static void GenericInstanceFullName(this TypeReference self, StringBuilder builder)
        {
            var gi = self as IGenericInstance;
            if (gi != null)
            {
                builder.Append("<");
                var arguments = gi.GenericArguments;
                Debug.Assert(arguments.Count > 0);
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (i > 0)
                        builder.Append(",");
                    builder.Append(arguments[i].GetFullTypeName());
                }
                builder.Append(">");
                return;
            }
            var gpp = self as IGenericParameterProvider;
            if (gpp != null && gpp.HasGenericParameters)
            {
                builder.Append("<");
                var arguments = gpp.GenericParameters;
                Debug.Assert(arguments.Count > 0);
                for (int i = 0; i < arguments.Count; i++)
                {
                    if (i > 0)
                        builder.Append(",");
                    builder.Append(arguments[i].GetFullTypeName());
                }
                builder.Append(">");
                return;
            }
        }

        public static bool IsResources(this AssemblyDefinition self)
        {
            //<Module>
            return self.Name.Name.EndsWith(".resources") &&
                self.Modules.Count == 1 && self.MainModule.Types.Count == 1;
        }

        public static bool IsNativeImage(this AssemblyDefinition self)
        {
            //TODO: Filter *.ni.*
            return false; //(self.MainModule.Attributes & ModuleAttributes.ILOnly) == 0;
        }
    }
}
