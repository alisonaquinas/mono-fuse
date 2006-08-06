//
// MapUtils.cs: Builds a C map of constants defined on C# land
//
// Authors:
//  Miguel de Icaza (miguel@novell.com)
//  Jonathan Pryor (jonpryor@vt.edu)
//
// (C) 2003 Novell, Inc.
// (C) 2004-2005 Jonathan Pryor
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;

using Mono.Unix.Native;

delegate void CreateFileHandler (string assembly_name, string file_prefix);
delegate void AssemblyAttributesHandler (Assembly assembly);
delegate void TypeHandler (Type t, string ns, string fn);
delegate void CloseFileHandler (string file_prefix);

class MakeMap {

	public static int Main (string [] args)
	{
		FileGenerator[] generators = new FileGenerator[]{
			new HeaderFileGenerator (),
			new SourceFileGenerator (),
			new ConvertFileGenerator (),
			new ConvertDocFileGenerator (),
		};

		MakeMap composite = new MakeMap ();
		foreach (FileGenerator g in generators) {
			composite.FileCreators += new CreateFileHandler (g.CreateFile);
			composite.AssemblyAttributesHandler += 
				new AssemblyAttributesHandler (g.WriteAssemblyAttributes);
			composite.TypeHandler += new TypeHandler (g.WriteType);
			composite.FileClosers += new CloseFileHandler (g.CloseFile);
		}

		return composite.Run (args);
	}

	event CreateFileHandler FileCreators;
	event AssemblyAttributesHandler AssemblyAttributesHandler;
	event TypeHandler TypeHandler;
	event CloseFileHandler FileClosers;

	int Run (string[] args)
	{
		if (args.Length != 2){
			Console.WriteLine ("Usage is: make-map assembly output");
			return 1;
		}
		
		string assembly_name = args[0];
		string output = args[1];

		FileCreators (assembly_name, output);

		Assembly assembly = Assembly.LoadFrom (assembly_name);
		AssemblyAttributesHandler (assembly);
		
		Type [] exported_types = assembly.GetTypes ();
		Array.Sort (exported_types, new TypeFullNameComparer ());
			
		foreach (Type t in exported_types) {
			string ns = MapUtils.GetNamespace (t);
			/*
			if (ns == null || !ns.StartsWith ("Mono"))
				continue;
			 */
			string fn = MapUtils.GetManagedType (t);

			TypeHandler (t, ns, fn);
		}
		FileClosers (output);

		return 0;
	}

	private class TypeFullNameComparer : IComparer<Type> {
		public int Compare (Type t1, Type t2)
		{
			if (t1 == t2)
				return 0;
			if (t1 == null)
				return 1;
			if (t2 == null)
				return -1;
			return CultureInfo.InvariantCulture.CompareInfo.Compare (
					t1.FullName, t2.FullName, CompareOptions.Ordinal);
		}
	}
}

static class MapUtils {
	public static T GetCustomAttribute <T> (MemberInfo element) where T : Attribute
	{
		return (T) Attribute.GetCustomAttribute (element, typeof(T), true);
	}

	public static T GetCustomAttribute <T> (Assembly assembly) where T : Attribute
	{
		return (T) Attribute.GetCustomAttribute (assembly, typeof(T), true);
	}

	public static T[] GetCustomAttributes <T> (MemberInfo element) where T : Attribute
	{
		return (T[]) Attribute.GetCustomAttributes (element, typeof(T), true);
	}

	public static T[] GetCustomAttributes <T> (Assembly assembly) where T : Attribute
	{
		return (T[]) Attribute.GetCustomAttributes (assembly, typeof(T), true);
	}

	public static bool IsIntegralType (Type t)
	{
		return t == typeof(byte) || t == typeof(sbyte) || t == typeof(char) ||
			t == typeof(short) || t == typeof(ushort) || 
			t == typeof(int) || t == typeof(uint) || 
			t == typeof(long) || t == typeof(ulong);
	}

	public static string GetNativeType (Type t)
	{
		Type et = GetElementType (t);
		string ut = et.Name;
		if (et.IsEnum)
			ut = Enum.GetUnderlyingType (et).Name;

		string type = null;

		switch (ut) {
			case "Boolean":       type = "int";             break;
			case "Byte":          type = "unsigned char";   break;
			case "SByte":         type = "signed char";     break;
			case "Int16":         type = "short";           break;
			case "UInt16":        type = "unsigned short";  break;
			case "Int32":         type = "int";             break;
			case "UInt32":        type = "unsigned int";    break;
			case "Int64":         type = "gint64";          break;
			case "UInt64":        type = "guint64";         break;
			case "IntPtr":        type = "void*";           break;
			case "UIntPtr":       type = "void*";           break;
			case "String":        type = "const char";      break; /* ref type */
			case "StringBuilder": type = "char";            break; /* ref type */
			case "Void":          type = "void";            break;
			case "HandleRef":     type = "void*";           break;
		}
		bool isDelegate = typeof(Delegate).IsAssignableFrom (t);
		if (type == null)
			type = isDelegate ? t.Name : GetStructName (t);
		if (!et.IsValueType && !isDelegate) {
			type += "*";
		}
		while (t.HasElementType) {
			t = t.GetElementType ();
			type += "*";
		}
		return type;
		//return (t.IsByRef || t.IsArray || (!t.IsValueType && !isDelegate)) ? type + "*" : type;
	}

	private static string GetStructName (Type t)
	{
		t = GetElementType (t);
		return "struct " + GetManagedType (t);
	}

	public static Type GetElementType (Type t)
	{
		while (t.HasElementType) {
			t = t.GetElementType ();
		}
		return t;
	}

	public static string GetNamespace (Type t)
	{
		MapAttribute map = MapUtils.GetCustomAttribute <MapAttribute> (t);
		if (map != null && map.ExportPrefix != null) {
			return map.ExportPrefix;
		}
#if true
		/* this is legacy behavior; Mono.Posix.dll should be fixed to use
		 * MapAttribute.ExportPrefix so we don't need this hack anymore */
		if (t.Namespace == "Mono.Unix.Native" || t.Namespace == "Mono.Unix")
			return "Mono_Posix";
#endif
		return t.Namespace != null ? t.Namespace.Replace ('.', '_') : "";
	}

	public static string GetManagedType (Type t)
	{
		string ns = GetNamespace (t);
		string tn = 
			(t.DeclaringType != null ? t.DeclaringType.Name + "_" : "") + t.Name;
		return ns + "_" + tn;
	}

	public static string GetNativeType (FieldInfo field)
	{
		MapAttribute map = 
			GetCustomAttribute <MapAttribute> (field)
			??
			GetCustomAttribute <MapAttribute> (field.FieldType);
		if (map != null)
			return map.NativeType;
		return null;
	}

	public static string GetFunctionDeclaration (string name, MethodInfo method)
	{
		StringBuilder sb = new StringBuilder ();
#if false
		Console.WriteLine (t);
		foreach (object o in t.GetMembers ())
			Console.WriteLine ("\t" + o);
#endif
		sb.Append (method.ReturnType == typeof(string) 
				? "char*" 
				: MapUtils.GetNativeType (method.ReturnType));
		sb.Append (" ").Append (name).Append (" (");


		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length == 0) {
			sb.Append ("void");
		}
		else {
			if (parameters.Length > 0) {
				WriteParameterDeclaration (sb, parameters [0]);
			}
			for (int i = 1; i < parameters.Length; ++i) {
				sb.Append (", ");
				WriteParameterDeclaration (sb, parameters [i]);
			}
		}
		sb.Append (")");
		return sb.ToString ();
	}

	private static void WriteParameterDeclaration (StringBuilder sb, ParameterInfo pi)
	{
		// DumpTypeInfo (pi.ParameterType);
		string nt = GetNativeType (pi.ParameterType);
		sb.AppendFormat ("{0} {1}", nt, pi.Name);
	}

	internal class _MemberNameComparer : IComparer<MemberInfo>, IComparer <FieldInfo> {
		public int Compare (FieldInfo m1, FieldInfo m2)
		{
			return Compare ((MemberInfo) m1, (MemberInfo) m2);
		}

		public int Compare (MemberInfo m1, MemberInfo m2)
		{
			if (m1 == m2)
				return 0;
			if (m1 == null)
				return 1;
			if (m2 == null)
				return -1;
			return CultureInfo.InvariantCulture.CompareInfo.Compare (
					m1.Name, m2.Name, CompareOptions.Ordinal);
		}
	}

	private class _OrdinalStringComparer : IComparer<string> {
		public int Compare (string s1, string s2)
		{
			if (object.ReferenceEquals (s1, s2))
				return 0;
			if (s1 == null)
				return 1;
			if (s2 == null)
				return -1;
			return CultureInfo.InvariantCulture.CompareInfo.Compare (s1, s2, 
					CompareOptions.Ordinal);
		}
	}

	internal static _MemberNameComparer MemberNameComparer = new _MemberNameComparer ();
	internal static IComparer<string> OrdinalStringComparer = new _OrdinalStringComparer ();
}

abstract class FileGenerator {
	public abstract void CreateFile (string assembly_name, string file_prefix);

	public virtual void WriteAssemblyAttributes (Assembly assembly)
	{
	}

	public abstract void WriteType (Type t, string ns, string fn);
	public abstract void CloseFile (string file_prefix);

	protected static void WriteHeader (StreamWriter s, string assembly)
	{
		WriteHeader (s, assembly, false);
	}

	protected static void WriteHeader (StreamWriter s, string assembly, bool noConfig)
	{
		s.WriteLine (
			"/*\n" +
			" * This file was automatically generated by make-map from {0}.\n" +
			" *\n" +
			" * DO NOT MODIFY.\n" +
			" */",
			assembly);
		if (!noConfig) {
			s.WriteLine ("#include <config.h>");
		}
		s.WriteLine ();
	}

	protected static bool CanMapType (Type t)
	{
		return MapUtils.GetCustomAttributes <MapAttribute> (t).Length > 0;
	}

	protected static bool IsFlagsEnum (Type t)
	{
		return t.IsEnum && 
			MapUtils.GetCustomAttributes <FlagsAttribute> (t).Length > 0;
	}

	protected static void SortFieldsInOffsetOrder (Type t, FieldInfo[] fields)
	{
		Array.Sort (fields, delegate (FieldInfo f1, FieldInfo f2) {
				long o1 = (long) Marshal.OffsetOf (f1.DeclaringType, f1.Name);
				long o2 = (long) Marshal.OffsetOf (f2.DeclaringType, f2.Name);
				return o1.CompareTo (o2);
		});
	}
}

class HeaderFileGenerator : FileGenerator {
	StreamWriter sh;
	string assembly_file;
	Dictionary<string, MethodInfo>  methods   = new Dictionary <string, MethodInfo> ();
	Dictionary<string, Type>        structs   = new Dictionary <string, Type> ();
	Dictionary<string, MethodInfo>  delegates = new Dictionary <string, MethodInfo> ();

	public override void CreateFile (string assembly_name, string file_prefix)
	{
		sh = File.CreateText (file_prefix + ".h");
		file_prefix = file_prefix.Replace ("../", "");
		this.assembly_file = assembly_name;
		WriteHeader (sh, assembly_name);
		assembly_name = assembly_name.Replace (".dll", "").Replace (".", "_");
		sh.WriteLine ("#ifndef INC_" + assembly_name + "_" + file_prefix + "_H");
		sh.WriteLine ("#define INC_" + assembly_name + "_" + file_prefix + "_H\n");
		sh.WriteLine ("#include <glib/gtypes.h>\n");
		sh.WriteLine ("G_BEGIN_DECLS\n");

		sh.WriteLine ("/*\n * Enumerations\n */");
		// Kill warning about unused method
		DumpTypeInfo (null);
	}

	public override void WriteAssemblyAttributes (Assembly assembly)
	{
		HeaderAttribute hattr = MapUtils.GetCustomAttribute <HeaderAttribute> (assembly);
		if (hattr != null) {
			WriteDefines (sh, hattr);
			WriteIncludes (sh, hattr);
		}
		sh.WriteLine ();
	}

	static void WriteDefines (TextWriter writer, HeaderAttribute hattr)
	{
		string [] defines = hattr.Defines.Split (',');
		foreach (string def in defines) {
			if (def.Length == 0)
				continue;
			string[] val = def.Split ('=');
			writer.WriteLine ("#ifndef {0}", val [0]);
			writer.WriteLine ("#define {0}{1}", val [0], 
					val.Length > 1 ? " " + val [1] : "");
			writer.WriteLine ("#endif /* ndef {0} */", def);
		}
	}

	static void WriteIncludes (TextWriter writer, HeaderAttribute hattr)
	{
		string [] includes = hattr.Includes.Split (',');
		foreach (string inc in includes){
			if (inc.Length == 0)
				continue;
			if (inc.Length > 3 && 
					string.CompareOrdinal (inc, 0, "ah:", 0, 3) == 0) {
				string i = inc.Substring (3);
				writer.WriteLine ("#ifdef HAVE_" + (i.ToUpper ().Replace ("/", "_").Replace (".", "_")));
				writer.WriteLine ("#include <{0}>", i);
				writer.WriteLine ("#endif");
			} else 
				writer.WriteLine ("#include <{0}>", inc);
		}
	}

	public override void WriteType (Type t, string ns, string fn)
	{
		WriteEnum (t, ns, fn);
		CacheStructs (t, ns, fn);
		CacheExternalMethods (t, ns, fn);
	}

	private void WriteEnum (Type t, string ns, string fn)
	{
		if (!CanMapType (t) || !t.IsEnum)
			return;

		string etype = MapUtils.GetNativeType (t);

		WriteLiteralValues (sh, t, fn);
		sh.WriteLine ("int {1}_From{2} ({0} x, {0} *r);", etype, ns, t.Name);
		sh.WriteLine ("int {1}_To{2} ({0} x, {0} *r);", etype, ns, t.Name);
		sh.WriteLine ();
	}

	static void WriteLiteralValues (StreamWriter sh, Type t, string n)
	{
		object inst = Activator.CreateInstance (t);
		int max_field_length = 0;
		FieldInfo[] fields = t.GetFields ();
		Array.Sort (fields, delegate (FieldInfo f1, FieldInfo f2) {
				max_field_length = Math.Max (max_field_length, f1.Name.Length);
				max_field_length = Math.Max (max_field_length, f2.Name.Length);
				return MapUtils.MemberNameComparer.Compare (f1, f2);
		});
		max_field_length += 1 + n.Length;
		sh.WriteLine ("enum {0} {{", n);
		foreach (FieldInfo fi in fields) {
			if (!fi.IsLiteral)
				continue;
			string e = n + "_" + fi.Name;
			sh.WriteLine ("\t{0,-" + max_field_length + "} = 0x{1:x},", 
					e, fi.GetValue (inst));
			sh.WriteLine ("\t#define {0,-" + max_field_length + "} {0}", e);
		}
		sh.WriteLine ("};");
	}


	private void CacheStructs (Type t, string ns, string fn)
	{
		if (t.IsEnum)
			return;
		if (MapUtils.GetCustomAttributes <MapAttribute> (t).Length > 0) {
			structs [t.Name] = t;
		}
	}

	private void CacheExternalMethods (Type t, string ns, string fn)
	{
		BindingFlags bf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		foreach (MethodInfo m in t.GetMethods (bf)) {
			if ((m.Attributes & MethodAttributes.PinvokeImpl) == 0)
				continue;
			DllImportAttribute dia = GetDllImportInfo (m);
			if (dia == null) {
				Console.WriteLine ("Unable to emit native prototype for P/Invoke " + 
						"method: {0}", m);
				continue;
			}
			// we shouldn't declare prototypes for POSIX, etc. functions.
			if (dia.Value != "MonoFuseHelper" || IsOnExcludeList (dia.EntryPoint))
				continue;
			methods [dia.EntryPoint] = m;
			RecordTypes (m);
		}
	}

	private static DllImportAttribute GetDllImportInfo (MethodInfo method)
	{
		// .NET 2.0 synthesizes pseudo-attributes such as DllImport
		DllImportAttribute dia = MapUtils.GetCustomAttribute <DllImportAttribute> (method);
		if (dia != null)
			return dia;

		// We're not on .NET 2.0; assume we're on Mono and use some internal
		// methods...
		Type MonoMethod = Type.GetType ("System.Reflection.MonoMethod", false);
		if (MonoMethod == null) {
			Console.WriteLine ("cannot find MonoMethod");
			return null;
		}
		MethodInfo GetDllImportAttribute = 
			MonoMethod.GetMethod ("GetDllImportAttribute", 
					BindingFlags.Static | BindingFlags.NonPublic);
		if (GetDllImportAttribute == null) {
			Console.WriteLine ("cannot find GetDllImportAttribute");
			return null;
		}
		IntPtr mhandle = method.MethodHandle.Value;
		return (DllImportAttribute) GetDllImportAttribute.Invoke (null, 
				new object[]{mhandle});
	}

	private static string[] ExcludeList = new string[]{
		"Mono_Posix_Stdlib_snprintf",
	};

	private bool IsOnExcludeList (string method)
	{
		int idx = Array.BinarySearch (ExcludeList, method);
		return (idx >= 0 && idx < ExcludeList.Length) ? true : false;
	}

	private void RecordTypes (MethodInfo method)
	{
		ParameterInfo[] parameters = method.GetParameters ();
		foreach (ParameterInfo pi in parameters) {
			RecordTypes (pi.ParameterType);
		}
	}

	private void RecordTypes (Type st)
	{
		if (!delegates.ContainsKey (st.Name) && typeof(Delegate).IsAssignableFrom (st)) {
			MethodInfo mi = st.GetMethod ("Invoke");
			delegates [st.Name] = mi;
			RecordTypes (mi);
			return;
		}
		Type et = MapUtils.GetElementType (st);
		string s = MapUtils.GetNativeType (et);
		if (s.StartsWith ("struct ") && !structs.ContainsKey (et.Name)) {
			structs [et.Name] = et;
			foreach (FieldInfo fi in et.GetFields (BindingFlags.Instance | 
					BindingFlags.Public | BindingFlags.NonPublic)) {
				RecordTypes (fi.FieldType);
			}
		}
	}

	public override void CloseFile (string file_prefix)
	{
		IEnumerable<string> structures = Sort (structs.Keys);
		sh.WriteLine ("/*\n * Structure Declarations\n */\n");
		foreach (string s in structures) {
			sh.WriteLine ("struct {0};", MapUtils.GetManagedType (structs [s]));
		}

		sh.WriteLine ("/*\n * Delegate Declarations\n */\n");
		foreach (string s in Sort (delegates.Keys)) {
			sh.WriteLine ("typedef {0};",
					MapUtils.GetFunctionDeclaration ("(*" + s + ")", delegates [s]));
		}

		sh.WriteLine ("/*\n * Structures\n */\n");
		foreach (string s in structures) {
			WriteStructDeclarations (s);
		}

		sh.WriteLine ();

		sh.WriteLine ("/*\n * Functions\n */");
		foreach (string method in Sort (methods.Keys)) {
			WriteMethodDeclaration ((MethodInfo) methods [method], method);
		}

		sh.WriteLine ("\nG_END_DECLS\n");
		sh.WriteLine ("#endif /* ndef INC_Mono_Posix_" + file_prefix + "_H */\n");
		sh.Close ();
	}

	private static IEnumerable<string> Sort (ICollection<string> c)
	{
		List<string> al = new List<string> (c);
		al.Sort (MapUtils.OrdinalStringComparer);
		return al;
	}

	private void WriteStructDeclarations (string s)
	{
		Type t = structs [s];
		if (!t.Assembly.CodeBase.EndsWith (this.assembly_file)) {
			return;
		}
		sh.WriteLine ("struct {0} {{", MapUtils.GetManagedType (t));
		FieldInfo[] fields = t.GetFields (BindingFlags.Instance | 
				BindingFlags.Public | BindingFlags.NonPublic);
		int max_type_len = 0, max_name_len = 0, max_native_len = 0;
		Array.ForEach (fields, delegate (FieldInfo f) {
				max_type_len    = Math.Max (max_type_len, GetType (f.FieldType).Length);
				max_name_len    = Math.Max (max_name_len, f.Name.Length);
				string native_type = MapUtils.GetNativeType (f);
				if (native_type != null)
					max_native_len  = Math.Max (max_native_len, native_type.Length);
		});
		SortFieldsInOffsetOrder (t, fields);
		foreach (FieldInfo field in fields) {
			sh.Write ("\t{0,-" + max_type_len + "} {1};", 
					GetType (field.FieldType), field.Name);
			string native_type = MapUtils.GetNativeType (field);
			if (native_type != null) {
				sh.Write (new string (' ', max_name_len - field.Name.Length));
				sh.Write ("  /* {0,-" + max_native_len + "} */", native_type);
			}
			sh.WriteLine ();
		}
		sh.WriteLine ("};");
		if (MapUtils.GetCustomAttributes <MapAttribute> (t).Length > 0) {
			sh.WriteLine ();
			MapAttribute map = MapUtils.GetCustomAttribute <MapAttribute> (t);
			sh.WriteLine (
					"int\n{0}_From{1} ({3}{4} from, {2} *to);" + 
					"int\n{0}_To{1} ({2} *from, {3}{4} to);\n",
					MapUtils.GetNamespace (t), t.Name, map.NativeType, 
					MapUtils.GetNativeType (t), t.IsValueType ? "*" : "");
		}
		sh.WriteLine ();
	}

	private static string GetType (Type t)
	{
		if (typeof(Delegate).IsAssignableFrom (t))
			return t.Name;
		return MapUtils.GetNativeType (t);
	}

	private void WriteMethodDeclaration (MethodInfo method, string entryPoint)
	{
		if (method.ReturnType.IsClass) {
			Console.WriteLine ("warning: {0} has a return type of {1}, which is a reference type",
					entryPoint, method.ReturnType.FullName);
		}
		sh.Write (MapUtils.GetFunctionDeclaration (entryPoint, method));
		sh.WriteLine (";");

#if false
		sh.WriteLine ("{0} ", method.ReturnType == typeof(string) 
				? "char*" 
				: MapUtils.GetNativeType (method.ReturnType));
		sh.Write ("{0} ", entryPoint);
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length == 0) {
			sh.WriteLine ("(void);");
			return;
		}
		if (parameters.Length > 0) {
			sh.Write ("(");
			WriteParameterDeclaration (parameters [0]);
		}
		for (int i = 1; i < parameters.Length; ++i) {
			sh.Write (", ");
			WriteParameterDeclaration (parameters [i]);
		}
		sh.WriteLine (");");
#endif
	}

	private void DumpTypeInfo (Type t)
	{
		if (t == null)
			return;

		sh.WriteLine ("\t\t/* Type Info for " + t.FullName + ":");
		foreach (MemberInfo mi in typeof(Type).GetMembers()) {
			sh.WriteLine ("\t\t\t{0}={1}", mi.Name, GetMemberValue (mi, t));
		}
		sh.WriteLine ("\t\t */");
	}

	private static string GetMemberValue (MemberInfo mi, Type t)
	{
		try {
		switch (mi.MemberType) {
			case MemberTypes.Constructor:
			case MemberTypes.Method: {
				MethodBase b = (MethodBase) mi;
				if (b.GetParameters().Length == 0)
					return b.Invoke (t, new object[]{}).ToString();
				return "<<cannot invoke>>";
			}
			case MemberTypes.Field:
				return ((FieldInfo) mi).GetValue (t).ToString ();
			case MemberTypes.Property: {
				PropertyInfo pi = (PropertyInfo) mi;
				if (!pi.CanRead)
					return "<<cannot read>>";
				return pi.GetValue (t, null).ToString ();
			}
			default:
				return "<<unknown value>>";
		}
		}
		catch (Exception e) {
			return "<<exception reading member: " + e.Message + ">>";
		}
	}

	private void WriteParameterDeclaration (ParameterInfo pi)
	{
		// DumpTypeInfo (pi.ParameterType);
		string nt = MapUtils.GetNativeType (pi.ParameterType);
		sh.Write ("{0} {1}", nt, pi.Name);
	}
}

class SourceFileGenerator : FileGenerator {
	StreamWriter sc;
	string file_prefix;

	public override void CreateFile (string assembly_name, string file_prefix)
	{
		sc = File.CreateText (file_prefix + ".c");
		WriteHeader (sc, assembly_name);

		if (file_prefix.IndexOf ("/") != -1)
			file_prefix = file_prefix.Substring (file_prefix.IndexOf ("/") + 1);
		this.file_prefix = file_prefix;
		sc.WriteLine ("#include <stdlib.h>");
		sc.WriteLine ("#include <string.h>");
		sc.WriteLine ("#include <mono/posix/limits.h>");
		sc.WriteLine ();
	}

	public override void WriteAssemblyAttributes (Assembly assembly)
	{
		HeaderAttribute hattr = MapUtils.GetCustomAttribute <HeaderAttribute> (assembly);
		if (hattr != null) {
			WriteDefines (sc, hattr);
			WriteIncludes (sc, hattr);
		}
		sc.WriteLine ();
		sc.WriteLine ("#include \"{0}.h\"", file_prefix);
		sc.WriteLine ();
	}

	static void WriteDefines (TextWriter writer, HeaderAttribute hattr)
	{
		string [] defines = hattr.Defines.Split (',');
		foreach (string def in defines) {
			if (def.Length == 0)
				continue;
			string[] val = def.Split ('=');
			writer.WriteLine ("#ifndef {0}", val [0]);
			writer.WriteLine ("#define {0}{1}", val [0], 
					val.Length > 1 ? " " + val [1] : "");
			writer.WriteLine ("#endif /* ndef {0} */", def);
		}
	}

	static void WriteIncludes (TextWriter writer, HeaderAttribute hattr)
	{
		string [] includes = hattr.Includes.Split (',');
		foreach (string inc in includes){
			if (inc.Length == 0)
				continue;
			if (inc.Length > 3 && 
					string.CompareOrdinal (inc, 0, "ah:", 0, 3) == 0) {
				string i = inc.Substring (3);
				writer.WriteLine ("#ifdef HAVE_" + (i.ToUpper ().Replace ("/", "_").Replace (".", "_")));
				writer.WriteLine ("#include <{0}>", i);
				writer.WriteLine ("#endif");
			} else 
				writer.WriteLine ("#include <{0}>", inc);
		}
	}

	public override void WriteType (Type t, string ns, string fn)
	{
		if (!CanMapType (t))
			return;

		string etype = MapUtils.GetNativeType (t);

		if (t.IsEnum) {
			bool bits = IsFlagsEnum (t);

			WriteFromManagedEnum (t, ns, fn, etype, bits);
			WriteToManagedEnum (t, ns, fn, etype, bits);
		}
		else {
			WriteFromManagedClass (t, ns, fn, etype);
			WriteToManagedClass (t, ns, fn, etype);
		}
	}

	private void WriteFromManagedEnum (Type t, string ns, string fn, string etype, bool bits)
	{
		sc.WriteLine ("int {1}_From{2} ({0} x, {0} *r)", etype, ns, t.Name);
		sc.WriteLine ("{");
		sc.WriteLine ("\t*r = 0;");
		// For many values, 0 is a valid value, but doesn't have it's own symbol.
		// Examples: Error (0 means "no error"), WaitOptions (0 means "no options").
		// Make 0 valid for all conversions.
		sc.WriteLine ("\tif (x == 0)\n\t\treturn 0;");
		FieldInfo[] fields = t.GetFields ();
		Array.Sort<FieldInfo> (fields, MapUtils.MemberNameComparer);
		foreach (FieldInfo fi in fields) {
			if (!fi.IsLiteral)
				continue;
			if (Attribute.GetCustomAttribute (fi, 
				typeof(ObsoleteAttribute), false) != null) {
				sc.WriteLine ("\t/* {0}_{1} is obsolete; ignoring */", fn, fi.Name);
				continue;
			}
			if (bits)
				// properly handle case where [Flags] enumeration has helper
				// synonyms.  e.g. DEFFILEMODE and ACCESSPERMS for mode_t.
				sc.WriteLine ("\tif ((x & {0}_{1}) == {0}_{1})", fn, fi.Name);
			else
				sc.WriteLine ("\tif (x == {0}_{1})", fn, fi.Name);
			sc.WriteLine ("#ifdef {0}", fi.Name);
			if (bits)
				sc.WriteLine ("\t\t*r |= {1};", fn, fi.Name);
			else
				sc.WriteLine ("\t\t{{*r = {1}; return 0;}}", fn, fi.Name);
			sc.WriteLine ("#else /* def {0} */\n\t\t{{errno = EINVAL; return -1;}}", fi.Name);
			sc.WriteLine ("#endif /* ndef {0} */", fi.Name);
		}
		if (bits)
			sc.WriteLine ("\treturn 0;");
		else
			sc.WriteLine ("\terrno = EINVAL; return -1;"); // return error if not matched
		sc.WriteLine ("}\n");
	}

	private void WriteToManagedEnum (Type t, string ns, string fn, string etype, bool bits)
	{
		sc.WriteLine ("int {1}_To{2} ({0} x, {0} *r)", etype, ns, t.Name);
		sc.WriteLine ("{");
		sc.WriteLine ("\t*r = 0;", etype);
		// For many values, 0 is a valid value, but doesn't have it's own symbol.
		// Examples: Error (0 means "no error"), WaitOptions (0 means "no options").
		// Make 0 valid for all conversions.
		sc.WriteLine ("\tif (x == 0)\n\t\treturn 0;");
		FieldInfo[] fields = t.GetFields ();
		Array.Sort<FieldInfo> (fields, MapUtils.MemberNameComparer);
		foreach (FieldInfo fi in fields) {
			if (!fi.IsLiteral)
				continue;
			sc.WriteLine ("#ifdef {0}", fi.Name);
			if (bits)
				// properly handle case where [Flags] enumeration has helper
				// synonyms.  e.g. DEFFILEMODE and ACCESSPERMS for mode_t.
				sc.WriteLine ("\tif ((x & {1}) == {1})\n\t\t*r |= {0}_{1};", fn, fi.Name);
			else
				sc.WriteLine ("\tif (x == {1})\n\t\t{{*r = {0}_{1}; return 0;}}", fn, fi.Name);
			sc.WriteLine ("#endif /* ndef {0} */", fi.Name);
		}
		if (bits)
			sc.WriteLine ("\treturn 0;");
		else
			sc.WriteLine ("\terrno = EINVAL; return -1;");
		sc.WriteLine ("}\n");
	}

	private void WriteFromManagedClass (Type t, string ns, string fn, string etype)
	{
		MapAttribute map = MapUtils.GetCustomAttribute <MapAttribute> (t);
		sc.WriteLine ("int\n{0}_From{1} (struct {0}_{1} *from, {2} *to)",
				MapUtils.GetNamespace (t), t.Name, map.NativeType);
		WriteManagedClassConversion (t, delegate (FieldInfo field) {
				MapAttribute ft = MapUtils.GetCustomAttribute <MapAttribute> (field);
				if (ft != null)
					return ft.NativeType;
				return MapUtils.GetNativeType (field.FieldType);
		});
	}

	private delegate string GetFromType (FieldInfo field);

	private void WriteManagedClassConversion (Type t, GetFromType gft)
	{
		MapAttribute map = MapUtils.GetCustomAttribute <MapAttribute> (t);
		sc.WriteLine ("{");
		FieldInfo[] fields = GetFieldsToCopy (t);
		SortFieldsInOffsetOrder (t, fields);
		int max_len = 0;
		foreach (FieldInfo f in fields) {
			max_len = Math.Max (max_len, f.Name.Length);
			if (!MapUtils.IsIntegralType (f.FieldType))
				continue;
			string d = GetAutoconfDefine (map, f);
			if (d != null)
				sc.WriteLine ("#ifdef " + d);
			sc.WriteLine ("\tmph_return_val_if_overflow ({0}, from->{1}, -1);",
					gft (f), f.Name);
			if (d != null)
				sc.WriteLine ("#endif /* ndef " + d + " */");
		}
		sc.WriteLine ("\n\tmemset (to, 0, sizeof(*to));\n");
		foreach (FieldInfo f in fields) {
			string d = GetAutoconfDefine (map, f);
			if (d != null)
				sc.WriteLine ("#ifdef " + d);
			sc.WriteLine ("\tto->{0,-" + max_len + "} = from->{0};", f.Name);
			if (d != null)
				sc.WriteLine ("#endif /* ndef " + d + " */");
		}
		sc.WriteLine ();
		sc.WriteLine ("\treturn 0;");
		sc.WriteLine ("}\n");
		sc.WriteLine ();
	}

	private void WriteToManagedClass (Type t, string ns, string fn, string etype)
	{
		MapAttribute map = MapUtils.GetCustomAttribute <MapAttribute> (t);
		sc.WriteLine ("int\n{0}_To{1} ({2} *from, struct {0}_{1} *to)", 
				MapUtils.GetNamespace (t), t.Name, map.NativeType);
		WriteManagedClassConversion (t, delegate (FieldInfo field) {
				return MapUtils.GetNativeType (field.FieldType);
		});
	}

	private static FieldInfo[] GetFieldsToCopy (Type t)
	{
		FieldInfo[] fields = t.GetFields (BindingFlags.Instance | 
				BindingFlags.Public | BindingFlags.NonPublic);
		int count = 0;
		for (int i = 0; i < fields.Length; ++i)
			if (MapUtils.GetCustomAttribute <NonSerializedAttribute> (fields [i]) == null)
				++count;
		FieldInfo[] rf = new FieldInfo [count];
		for (int i = 0, j = 0; i < fields.Length; ++i) {
			if (MapUtils.GetCustomAttribute <NonSerializedAttribute> (fields [i]) == null)
				rf [j++] = fields [i];
		}
		return rf;
	}

	private static string GetAutoconfDefine (MapAttribute typeMap, FieldInfo field)
	{
		// this requires .NET 2.0 :-(
		// OptionalFieldAttribute opt = MapUtils.GetCustomAttribute <OptionalFieldAttribute> (field);
		MapAttribute fieldMap = MapUtils.GetCustomAttribute <MapAttribute> (field);
		if (fieldMap != null && fieldMap.Optional) {
			return string.Format ("HAVE_{0}_{1}", 
					typeMap.NativeType.ToUpperInvariant().Replace (" ", "_"),
					field.Name.ToUpperInvariant ());
		}
		return null;
	}

	public override void CloseFile (string file_prefix)
	{
		sc.Close ();
	}
}

class ConvertFileGenerator : FileGenerator {
	StreamWriter scs;

	public override void CreateFile (string assembly_name, string file_prefix)
	{
		scs = File.CreateText (file_prefix + ".cs");
		WriteHeader (scs, assembly_name, true);
		scs.WriteLine ("using System;");
		scs.WriteLine ("using System.Runtime.InteropServices;");
		scs.WriteLine ("using Mono.Unix.Native;\n");
		scs.WriteLine ("namespace Mono.Unix.Native {\n");
		scs.WriteLine ("\t[CLSCompliant (false)]");
		scs.WriteLine ("\tpublic sealed /* static */ partial class NativeConvert");
		scs.WriteLine ("\t{");
		scs.WriteLine ("\t\tprivate NativeConvert () {}\n");
		scs.WriteLine ("\t\tprivate const string LIB = \"MonoPosixHelper\";\n");
		scs.WriteLine ("\t\tprivate static void ThrowArgumentException (object value)");
		scs.WriteLine ("\t\t{");
		scs.WriteLine ("\t\t\tthrow new ArgumentOutOfRangeException (\"value\", value,");
		scs.WriteLine ("\t\t\t\tLocale.GetText (\"Current platform doesn't support this value.\"));");
		scs.WriteLine ("\t\t}\n");
	}

	public override void WriteType (Type t, string ns, string fn)
	{
		if (!CanMapType (t) || !t.IsEnum)
			return;

		string mtype = Enum.GetUnderlyingType(t).Name;
		ObsoleteAttribute oa = MapUtils.GetCustomAttribute <ObsoleteAttribute> (t);
		string obsolete = "";
		if (oa != null) {
			obsolete = string.Format ("[Obsolete (\"{0}\", {1})]\n\t\t",
					oa.Message, oa.IsError ? "true" : "false");
		}
		scs.WriteLine ("\t\t{3}[DllImport (LIB, " + 
			"EntryPoint=\"{0}_From{1}\")]\n" +
			"\t\tprivate static extern int From{1} ({1} value, out {2} rval);\n",
			ns, t.Name, mtype, obsolete);
		scs.WriteLine ("\t\t{3}public static bool TryFrom{1} ({1} value, out {2} rval)\n" +
			"\t\t{{\n" +
			"\t\t\treturn From{1} (value, out rval) == 0;\n" +
			"\t\t}}\n", ns, t.Name, mtype, obsolete);
		scs.WriteLine ("\t\t{2}public static {0} From{1} ({1} value)", mtype, t.Name, obsolete);
		scs.WriteLine ("\t\t{");
		scs.WriteLine ("\t\t\t{0} rval;", mtype);
		scs.WriteLine ("\t\t\tif (From{0} (value, out rval) == -1)\n" + 
				"\t\t\t\tThrowArgumentException (value);", t.Name);
		scs.WriteLine ("\t\t\treturn rval;");
		scs.WriteLine ("\t\t}\n");
		scs.WriteLine ("\t\t{3}[DllImport (LIB, " + 
			"EntryPoint=\"{0}_To{1}\")]\n" +
			"\t\tprivate static extern int To{1} ({2} value, out {1} rval);\n",
			ns, t.Name, mtype, obsolete);
		scs.WriteLine ("\t\t{2}public static bool TryTo{1} ({0} value, out {1} rval)\n" +
			"\t\t{{\n" +
			"\t\t\treturn To{1} (value, out rval) == 0;\n" +
			"\t\t}}\n", mtype, t.Name, obsolete);
		scs.WriteLine ("\t\t{2}public static {1} To{1} ({0} value)", mtype, t.Name, obsolete);
		scs.WriteLine ("\t\t{");
		scs.WriteLine ("\t\t\t{0} rval;", t.Name);
		scs.WriteLine ("\t\t\tif (To{0} (value, out rval) == -1)\n" + 
				"\t\t\t\tThrowArgumentException (value);", t.Name);
		scs.WriteLine ("\t\t\treturn rval;");
		scs.WriteLine ("\t\t}\n");
	}

	public override void CloseFile (string file_prefix)
	{
		scs.WriteLine ("\t}");
		scs.WriteLine ("}\n");
		scs.Close ();
	}
}

class ConvertDocFileGenerator : FileGenerator {
	StreamWriter scs;

	public override void CreateFile (string assembly_name, string file_prefix)
	{
		scs = File.CreateText (file_prefix + ".xml");
		scs.WriteLine ("    <!-- BEGIN GENERATED CONTENT");
		WriteHeader (scs, assembly_name, true);
		scs.WriteLine ("      -->");
	}

	public override void WriteType (Type t, string ns, string fn)
	{
		if (!CanMapType (t) || !t.IsEnum)
			return;

		bool bits = IsFlagsEnum (t);

		string type = GetCSharpType (t);
		string mtype = Enum.GetUnderlyingType(t).FullName;
		string member = t.Name;
		string ftype = t.FullName;

		string to_returns = "";
		string to_remarks = "";
		string to_exception = "";

		if (bits) {
			to_returns = "<returns>An approximation of the equivalent managed value.</returns>";
			to_remarks = @"<para>The current conversion functions are unable to determine
        if a value in a <c>[Flags]</c>-marked enumeration <i>does not</i> 
        exist on the current platform.  As such, if <paramref name=""value"" /> 
        contains a flag value which the current platform doesn't support, it 
        will not be present in the managed value returned.</para>
        <para>This should only be a problem if <paramref name=""value"" /> 
        <i>was not</i> previously returned by 
        <see cref=""M:Mono.Unix.Native.NativeConvert.From" + member + "\" />.</para>\n";
		}
		else {
			to_returns = "<returns>The equivalent managed value.</returns>";
			to_exception = @"
        <exception cref=""T:System.ArgumentOutOfRangeException"">
          <paramref name=""value"" /> has no equivalent managed value.
        </exception>
";
		}
		scs.WriteLine (@"
    <Member MemberName=""TryFrom{1}"">
      <MemberSignature Language=""C#"" Value=""public static bool TryFrom{1} ({0} value, out {2} rval);"" />
      <MemberType>Method</MemberType>
      <ReturnValue>
        <ReturnType>System.Boolean</ReturnType>
      </ReturnValue>
      <Parameters>
        <Parameter Name=""value"" Type=""{0}"" />
        <Parameter Name=""rval"" Type=""{3}&amp;"" RefType=""out"" />
      </Parameters>
      <Docs>
        <param name=""value"">The managed value to convert.</param>
        <param name=""rval"">The OS-specific equivalent value.</param>
        <summary>Converts a <see cref=""T:{0}"" /> 
          enumeration value to an OS-specific value.</summary>
        <returns><see langword=""true"" /> if the conversion was successful; 
        otherwise, <see langword=""false"" />.</returns>
        <remarks><para>This is an exception-safe alternative to 
        <see cref=""M:Mono.Unix.Native.NativeConvert.From{1}"" />.</para>
        <para>If successful, this method stores the OS-specific equivalent
        value of <paramref name=""value"" /> into <paramref name=""rval"" />.
        Otherwise, <paramref name=""rval"" /> will contain <c>0</c>.</para>
        </remarks>
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.From{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.To{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryTo{1}"" />
      </Docs>
    </Member>
    <Member MemberName=""From{1}"">
      <MemberSignature Language=""C#"" Value=""public static {2} From{1} ({0} value);"" />
      <MemberType>Method</MemberType>
      <ReturnValue>
        <ReturnType>{3}</ReturnType>
      </ReturnValue>
      <Parameters>
        <Parameter Name=""value"" Type=""{0}"" />
      </Parameters>
      <Docs>
        <param name=""value"">The managed value to convert.</param>
        <summary>Converts a <see cref=""T:{0}"" /> 
          to an OS-specific value.</summary>
        <returns>The equivalent OS-specific value.</returns>
        <exception cref=""T:System.ArgumentOutOfRangeException"">
          <paramref name=""value"" /> has no equivalent OS-specific value.
        </exception>
        <remarks></remarks>
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.To{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryFrom{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryTo{1}"" />
      </Docs>
    </Member>
    <Member MemberName=""TryTo{1}"">
      <MemberSignature Language=""C#"" Value=""public static bool TryTo{1} ({2} value, out {0} rval);"" />
      <MemberType>Method</MemberType>
      <ReturnValue>
        <ReturnType>System.Boolean</ReturnType>
      </ReturnValue>
      <Parameters>
        <Parameter Name=""value"" Type=""{3}"" />
        <Parameter Name=""rval"" Type=""{0}&amp;"" RefType=""out"" />
      </Parameters>
      <Docs>
        <param name=""value"">The OS-specific value to convert.</param>
        <param name=""rval"">The managed equivalent value</param>
        <summary>Converts an OS-specific value to a 
          <see cref=""T:{0}"" />.</summary>
        <returns><see langword=""true"" /> if the conversion was successful; 
        otherwise, <see langword=""false"" />.</returns>
        <remarks><para>This is an exception-safe alternative to 
        <see cref=""M:Mono.Unix.Native.NativeConvert.To{1}"" />.</para>
        <para>If successful, this method stores the managed equivalent
        value of <paramref name=""value"" /> into <paramref name=""rval"" />.
        Otherwise, <paramref name=""rval"" /> will contain a <c>0</c>
        cast to a <see cref=""T:{0}"" />.</para>
        " + to_remarks + 
@"        </remarks>
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.From{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.To{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryFrom{1}"" />
      </Docs>
    </Member>
    <Member MemberName=""To{1}"">
      <MemberSignature Language=""C#"" Value=""public static {0} To{1} ({2} value);"" />
      <MemberType>Method</MemberType>
      <ReturnValue>
        <ReturnType>{0}</ReturnType>
      </ReturnValue>
      <Parameters>
        <Parameter Name=""value"" Type=""{3}"" />
      </Parameters>
      <Docs>
        <param name=""value"">The OS-specific value to convert.</param>
        <summary>Converts an OS-specific value to a 
          <see cref=""T:{0}"" />.</summary>
					" + to_returns + "\n" + 
			to_exception + 
@"        <remarks>
        " + to_remarks + @"
        </remarks>
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.From{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryFrom{1}"" />
        <altmember cref=""M:Mono.Unix.Native.NativeConvert.TryTo{1}"" />
      </Docs>
    </Member>
", ftype, member, type, mtype
		);
	}

	private string GetCSharpType (Type t)
	{
		string ut = t.Name;
		if (t.IsEnum)
			ut = Enum.GetUnderlyingType (t).Name;
		Type et = t.GetElementType ();
		if (et != null && et.IsEnum)
			ut = Enum.GetUnderlyingType (et).Name;

		string type = null;

		switch (ut) {
			case "Boolean":       type = "bool";    break;
			case "Byte":          type = "byte";    break;
			case "SByte":         type = "sbyte";   break;
			case "Int16":         type = "short";   break;
			case "UInt16":        type = "ushort";  break;
			case "Int32":         type = "int";     break;
			case "UInt32":        type = "uint";    break;
			case "Int64":         type = "long";    break;
			case "UInt64":        type = "ulong";   break;
		}

		return type;
	}

	public override void CloseFile (string file_prefix)
	{
		scs.WriteLine ("    <!-- END GENERATED CONTENT -->");
		scs.Close ();
	}
}

// vim: noexpandtab