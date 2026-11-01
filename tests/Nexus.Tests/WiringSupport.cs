using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Nexus.Core;

namespace Nexus.Tests;

/// <summary>
/// Apoio dos testes de fiação (<see cref="WiringTests"/>): índice dos binários compilados (strings literais, constantes, tipos e
/// propriedades, lidos por <see cref="MetadataReader"/>, sem carregar o Nexus.Desktop, que é WPF) e um leitor léxico mínimo de C#
/// (sabe pular comentários, strings, strings interpoladas) para achar os pontos que montam <c>ActionRequest</c>.
/// </summary>
internal static partial class Wiring
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Nexus.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    });

    public static string Root => RootLazy.Value;

    public static string Config =>
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ? "Debug" : "Release";

    private static readonly Lazy<CatalogDocument> CatalogLazy = new(() => CatalogLoader.LoadDirectory(Path.Combine(Root, "catalog")));
    public static CatalogDocument Catalog => CatalogLazy.Value;

    public static CatalogEntry? Entry(string key) => Catalog.Actions.FirstOrDefault(a => a.Definition.Key == key);

    // ------------------------------------------------------------------ binários

    /// <summary>O .dll principal de cada projeto de src/ (compilado nesta configuração).</summary>
    public static IReadOnlyList<string> ProductAssemblies()
    {
        var list = new List<string>();
        foreach (var project in Directory.EnumerateDirectories(Path.Combine(Root, "src")))
        {
            var name = Path.GetFileName(project);
            var bin = Path.Combine(project, "bin", Config);
            if (!Directory.Exists(bin)) continue;
            var dll = Directory.EnumerateFiles(bin, name + ".dll", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (dll is not null) list.Add(dll);
        }
        Assert.True(list.Any(p => p.EndsWith("Nexus.Desktop.dll", StringComparison.Ordinal)),
            "Nexus.Desktop.dll não encontrado: compile a solução inteira antes dos testes.");
        return list;
    }

    public static string DesktopDll => ProductAssemblies().Single(p => p.EndsWith("Nexus.Desktop.dll", StringComparison.Ordinal));

    /// <summary>Strings literais (heap #US: todo ldstr) e constantes string (campos const) de um assembly.</summary>
    public static IEnumerable<string> Literals(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var result = new List<string>();
        var size = md.GetHeapSize(HeapIndex.UserString);
        if (size > 1)
        {
            var h = MetadataTokens.UserStringHandle(1);
            while (!h.IsNil)
            {
                result.Add(md.GetUserString(h));
                h = md.GetNextHandle(h);
            }
        }
        foreach (var (_, _, value) in ConstantFields(md))
            if (value is string s) result.Add(s);
        return result;
    }

    private static IEnumerable<(string Type, string Field, object? Value)> ConstantFields(MetadataReader md)
    {
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            var typeName = md.GetString(t.Name);
            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                if ((f.Attributes & FieldAttributes.Literal) == 0) continue;
                var ch = f.GetDefaultValue();
                if (ch.IsNil) continue;
                var c = md.GetConstant(ch);
                var reader = md.GetBlobReader(c.Value);
                object? value = c.TypeCode switch
                {
                    ConstantTypeCode.String => reader.ReadUTF16(reader.Length),
                    ConstantTypeCode.Int32 => reader.ReadInt32(),
                    ConstantTypeCode.Int64 => reader.ReadInt64(),
                    ConstantTypeCode.Boolean => reader.ReadBoolean(),
                    _ => null,
                };
                yield return (typeName, md.GetString(f.Name), value);
            }
        }
    }

    private static readonly Lazy<Dictionary<string, HashSet<string>>> ConstantsLazy = new(() =>
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var dll in ProductAssemblies())
        {
            using var pe = new PEReader(File.OpenRead(dll));
            var md = pe.GetMetadataReader();
            foreach (var (type, field, value) in ConstantFields(md))
            {
                if (value is null) continue;
                var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
                foreach (var k in new[] { $"{type}.{field}", field })
                {
                    if (!map.TryGetValue(k, out var set)) map[k] = set = new HashSet<string>(StringComparer.Ordinal);
                    set.Add(text);
                }
            }
        }
        return map;
    });

    /// <summary>Valores de constantes (const) dos binários, por "Tipo.Campo" e por "Campo".</summary>
    public static IReadOnlyDictionary<string, HashSet<string>> Constants => ConstantsLazy.Value;

    // ------------------------------------------------------------------ modelo de tipos (para XAML)

    public sealed record TypeInfo(string Namespace, string Name, string? BaseName, Dictionary<string, string> Properties);

    private static readonly Lazy<Dictionary<string, TypeInfo>> TypesLazy = new(() =>
    {
        var types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        foreach (var dll in ProductAssemblies())
        {
            using var pe = new PEReader(File.OpenRead(dll));
            var md = pe.GetMetadataReader();
            var provider = new NameProvider(md);
            foreach (var th in md.TypeDefinitions)
            {
                var t = md.GetTypeDefinition(th);
                var name = md.GetString(t.Name);
                if (!t.GetDeclaringType().IsNil) name = md.GetString(md.GetTypeDefinition(t.GetDeclaringType()).Name) + "+" + name;
                var ns = md.GetString(t.Namespace);
                string? baseName = t.BaseType.IsNil ? null : provider.Name(t.BaseType);
                var props = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var ph in t.GetProperties())
                {
                    var p = md.GetPropertyDefinition(ph);
                    var getter = p.GetAccessors().Getter;
                    if (getter.IsNil) continue;
                    var sig = p.DecodeSignature(provider, null);
                    props[md.GetString(p.Name)] = sig.ReturnType;
                }
                // Registros posicionais e campos públicos também são ligáveis? WPF só liga propriedades: campos ficam de fora.
                types.TryAdd(name, new TypeInfo(ns, name, baseName, props));
            }
        }
        return types;
    });

    /// <summary>Tipos de todos os binários, por nome simples (sem namespace; aninhados como "Externo+Interno").</summary>
    public static IReadOnlyDictionary<string, TypeInfo> Types => TypesLazy.Value;

    /// <summary>Tipo da propriedade (nome curto, genéricos como "ObservableCollection`1&lt;Linha&gt;"), subindo pela herança.</summary>
    public static string? PropertyType(string typeName, string property)
    {
        for (var name = typeName; name is not null && Types.TryGetValue(StripGenerics(name), out var t); name = t.BaseName)
            if (t.Properties.TryGetValue(property, out var type)) return type;
        return null;
    }

    public static bool TypeExists(string typeName) => Types.ContainsKey(StripGenerics(typeName));

    public static string StripGenerics(string name)
    {
        var i = name.IndexOf('<', StringComparison.Ordinal);
        return i < 0 ? name : name[..i];
    }

    /// <summary>Tipo do elemento de uma coleção (IEnumerable&lt;T&gt;, List&lt;T&gt;, ObservableCollection&lt;T&gt;, T[]).</summary>
    public static string? ElementType(string collectionType)
    {
        if (collectionType.EndsWith("[]", StringComparison.Ordinal)) return collectionType[..^2];
        var i = collectionType.IndexOf('<', StringComparison.Ordinal);
        if (i < 0 || !collectionType.EndsWith('>')) return null;
        var inner = collectionType[(i + 1)..^1];
        return inner.Contains(',', StringComparison.Ordinal) ? null : inner;
    }

    private sealed class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string Name(EntityHandle h) => h.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeFromDefinition(md, (TypeDefinitionHandle)h, 0),
            HandleKind.TypeReference => GetTypeFromReference(md, (TypeReferenceHandle)h, 0),
            HandleKind.TypeSpecification => GetTypeFromSpecification(md, null, (TypeSpecificationHandle)h, 0),
            _ => "?",
        };

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";
        public string GetByReferenceType(string elementType) => elementType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType == "Nullable`1" ? typeArguments[0] : $"{genericType}<{string.Join(",", typeArguments)}>";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var t = reader.GetTypeDefinition(handle);
            var name = reader.GetString(t.Name);
            return t.GetDeclaringType().IsNil ? name : reader.GetString(reader.GetTypeDefinition(t.GetDeclaringType()).Name) + "+" + name;
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var t = reader.GetTypeReference(handle);
            var name = reader.GetString(t.Name);
            return t.ResolutionScope.Kind == HandleKind.TypeReference
                ? GetTypeFromReference(reader, (TypeReferenceHandle)t.ResolutionScope, 0) + "+" + name
                : name;
        }
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }

    // ------------------------------------------------------------------ leitor léxico de C#

    /// <summary>Máscara: true onde o caractere é código (fora de comentário, string e char; buracos de interpolação contam como código).</summary>
    public static bool[] CodeMask(string s)
    {
        var mask = new bool[s.Length];
        var i = 0;
        LexCode(s, ref i, mask, stopAtBrace: false);
        return mask;
    }

    private static void LexCode(string s, ref int i, bool[] mask, bool stopAtBrace)
    {
        var depth = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2;
                continue;
            }
            if (c == '\'')
            {
                mask[i] = true; // a aspa simples conta como código (só o conteúdo é pulado)
                i++;
                while (i < s.Length && s[i] != '\'') i += s[i] == '\\' ? 2 : 1;
                if (i < s.Length) mask[i] = true;
                i++;
                continue;
            }
            if (c is '"' or '$' or '@' && StringStart(s, i, out var interpolated, out var verbatim, out var raw, out var contentStart))
            {
                for (var k = i; k < contentStart; k++) mask[k] = true;
                i = contentStart;
                LexString(s, ref i, mask, interpolated, verbatim, raw);
                continue;
            }
            if (stopAtBrace)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    if (depth == 0) return;
                    depth--;
                }
            }
            mask[i] = true;
            i++;
        }
    }

    private static bool StringStart(string s, int i, out bool interpolated, out bool verbatim, out bool raw, out int contentStart)
    {
        interpolated = verbatim = raw = false;
        var j = i;
        while (j < s.Length && s[j] is '$' or '@')
        {
            if (s[j] == '$') interpolated = true; else verbatim = true;
            j++;
        }
        contentStart = j + 1;
        if (j >= s.Length || s[j] != '"') return false;
        if (j + 2 < s.Length && s[j + 1] == '"' && s[j + 2] == '"')
        {
            raw = true;
            contentStart = j + 3;
        }
        // "$" sozinho seguido de outra coisa não é string; "@" antes de identificador é nome escapado.
        return true;
    }

    private static void LexString(string s, ref int i, bool[] mask, bool interpolated, bool verbatim, bool raw)
    {
        while (i < s.Length)
        {
            var c = s[i];
            if (raw)
            {
                if (c == '"' && i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"') { mask[i] = mask[i + 1] = mask[i + 2] = true; i += 3; return; }
            }
            else if (c == '\\' && !verbatim) { i += 2; continue; }
            else if (c == '"')
            {
                if (verbatim && i + 1 < s.Length && s[i + 1] == '"') { i += 2; continue; }
                mask[i] = true;
                i++;
                return;
            }
            if (interpolated && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { i += 2; continue; }
                mask[i] = true;
                i++;
                LexCode(s, ref i, mask, stopAtBrace: true);
                if (i < s.Length) { mask[i] = true; i++; }
                continue;
            }
            i++;
        }
    }

    /// <summary>Índice do fechamento que casa com o '(' / '[' / '{' em <paramref name="open"/> (só conta código).</summary>
    public static int MatchClose(string s, bool[] mask, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (!mask[i]) continue;
            var c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    /// <summary>Argumentos de nível superior (separados por vírgula) entre <paramref name="open"/> e <paramref name="close"/>.</summary>
    public static List<string> SplitArgs(string s, bool[] mask, int open, int close)
    {
        var list = new List<string>();
        var depth = 0;
        var generic = 0;
        var start = open + 1;
        for (var i = open + 1; i < close; i++)
        {
            if (!mask[i]) continue;
            var c = s[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '<' && GenericOpen(s, i)) generic++;
            else if (c == '>' && generic > 0) generic--;
            else if (c == ',' && depth == 0 && generic == 0)
            {
                list.Add(s[start..i].Trim());
                start = i + 1;
            }
        }
        var last = s[start..close].Trim();
        if (last.Length > 0 || list.Count > 0) list.Add(last);
        return list;
    }

    // "Dictionary<string, string>" dentro de um argumento: a vírgula dos genéricos não separa argumentos.
    private static bool GenericOpen(string s, int i) => i > 0 && char.IsLetterOrDigit(s[i - 1]) && GenericArgsRegex().IsMatch(s[i..Math.Min(s.Length, i + 80)]);

    [GeneratedRegex(@"\A<\s*[\w.?]+(\s*,\s*[\w.?]+)*\s*>")]
    private static partial Regex GenericArgsRegex();

    public static int LineOf(string s, int index) => s.AsSpan(0, index).Count('\n') + 1;

    // ------------------------------------------------------------------ IL: quem grava cada propriedade

    private static readonly Lazy<Dictionary<short, System.Reflection.Emit.OperandType>> OpCodeOperands = new(() =>
        typeof(System.Reflection.Emit.OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!)
            .ToDictionary(o => o.Value, o => o.OperandType));

    /// <summary>
    /// Propriedades ("Tipo.Prop") de um assembly que podem ter valor: getter calculado (não só lê o campo de apoio), ou campo de
    /// apoio/setter gravado em algum método do assembly (inicializador, construtor, outro tipo). As demais ficam sempre nulas.
    /// </summary>
    public static HashSet<string> PropertiesWithValue(string dll)
    {
        using var pe = new PEReader(File.OpenRead(dll));
        var md = pe.GetMetadataReader();
        var storedBy = new Dictionary<int, HashSet<int>>(); // campo -> métodos que gravam nele
        var calledMethods = new HashSet<int>();
        foreach (var th in md.TypeDefinitions)
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes();
                if (il is null) continue;
                foreach (var (op, token) in Instructions(il))
                {
                    if (token == 0) continue;
                    if (op is 0x7D or 0x80) // stfld, stsfld
                    {
                        if (!storedBy.TryGetValue(token, out var by)) storedBy[token] = by = [];
                        by.Add(MetadataTokens.GetToken(mh));
                    }
                    else if (op is 0x28 or 0x6F or 0x73) calledMethods.Add(token); // call, callvirt, newobj
                }
            }
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var th in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(th);
            var typeName = md.GetString(t.Name);
            var fields = t.GetFields().ToDictionary(f => md.GetString(md.GetFieldDefinition(f).Name), f => MetadataTokens.GetToken(f));
            foreach (var ph in t.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                var name = md.GetString(p.Name);
                var acc = p.GetAccessors();
                var has = false;
                var ownSetter = acc.Setter.IsNil ? 0 : MetadataTokens.GetToken(acc.Setter);
                // Gravado fora do próprio setter (o setter automático só repassa o valor de quem o chama).
                bool Stored(int field) => storedBy.TryGetValue(field, out var by) && by.Any(m => m != ownSetter);
                if (!acc.Getter.IsNil)
                {
                    var g = md.GetMethodDefinition(acc.Getter);
                    var body = g.RelativeVirtualAddress == 0 ? null : pe.GetMethodBody(g.RelativeVirtualAddress).GetILBytes();
                    // ldarg.0; ldfld <campo>; ret  (ou ldsfld <campo>; ret) = propriedade automática
                    // (o campo pode ser o de apoio ou o de um parâmetro de construtor primário: vale se for gravado em algum lugar)
                    int? read = body switch
                    {
                        [0x02, 0x7B, _, _, _, _, 0x2A] => BitConverter.ToInt32(body, 2),
                        [0x7E, _, _, _, _, 0x2A] => BitConverter.ToInt32(body, 1),
                        _ => null,
                    };
                    if (read is null || Stored(read.Value)) has = true;
                }
                if (fields.TryGetValue($"<{name}>k__BackingField", out var backing) && Stored(backing)) has = true;
                if (!acc.Setter.IsNil && calledMethods.Contains(MetadataTokens.GetToken(acc.Setter))) has = true;
                if (has) result.Add($"{typeName}.{name}");
            }
        }
        return result;
    }

    private static IEnumerable<(short Op, int Token)> Instructions(byte[] il)
    {
        var i = 0;
        while (i < il.Length)
        {
            short op = il[i++];
            if (op == 0xFE && i < il.Length) op = (short)(0xFE00 | il[i++]);
            if (!OpCodeOperands.Value.TryGetValue(op, out var kind)) yield break;
            var token = 0;
            switch (kind)
            {
                case System.Reflection.Emit.OperandType.InlineNone: break;
                case System.Reflection.Emit.OperandType.ShortInlineBrTarget:
                case System.Reflection.Emit.OperandType.ShortInlineI:
                case System.Reflection.Emit.OperandType.ShortInlineVar: i += 1; break;
                case System.Reflection.Emit.OperandType.InlineVar: i += 2; break;
                case System.Reflection.Emit.OperandType.InlineI8:
                case System.Reflection.Emit.OperandType.InlineR: i += 8; break;
                case System.Reflection.Emit.OperandType.InlineSwitch:
                    var n = BitConverter.ToInt32(il, i);
                    i += 4 + 4 * n;
                    break;
                case System.Reflection.Emit.OperandType.InlineField:
                case System.Reflection.Emit.OperandType.InlineMethod:
                case System.Reflection.Emit.OperandType.InlineTok:
                case System.Reflection.Emit.OperandType.InlineType:
                    token = BitConverter.ToInt32(il, i);
                    i += 4;
                    break;
                default: i += 4; break; // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR
            }
            yield return (op, token);
        }
    }
}
