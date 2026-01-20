using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;


public class F
{
    int i1;
    int i2;
    int i3;
    int i4;
    int i5;
    public int[] mas;

    public F()
    {
        i1 = 1; i2 = 2; i3 = 3; i4 = 4; i5 = 5;
        mas = new int[] { 1, 2 };
    }

    public F Get() { return new F(); }
}

public sealed class CsvReflectionSerializer<T> where T : new()
{
    private readonly MemberAccessor[] _members;
    private readonly string _headerLine;

    public CsvReflectionSerializer(bool includeNonPublic = true)
    {
        _members = BuildAccessors(includeNonPublic).ToArray();
        _headerLine = string.Join(",", _members.Select(m => EscapeCsv(m.Name)));
    }

    public string Serialize(T obj, bool includeHeader = true)
    {
        var sb = new StringBuilder(256);

        if (includeHeader)
            sb.AppendLine(_headerLine);

        for (int i = 0; i < _members.Length; i++)
        {
            if (i > 0) sb.Append(',');

            object value = _members[i].Getter(obj);
            string text = FormatValue(value, _members[i].Type);
            sb.Append(EscapeCsv(text));
        }

        sb.AppendLine();
        return sb.ToString();
    }

    public T Deserialize(string csv)
    {
        var lines = csv
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length < 2)
            throw new FormatException("CSV должен содержать заголовок и одну строку данных.");

        var header = SplitCsvLine(lines[0]);
        var data = SplitCsvLine(lines[1]);

        if (data.Count != header.Count)
            throw new FormatException("Количество столбцов заголовка и данных не совпадает.");

        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < header.Count; i++)
            map[header[i]] = i;

        var obj = new T();

        foreach (var member in _members)
        {
            int idx;
            if (!map.TryGetValue(member.Name, out idx))
                continue;

            string cell = data[idx];
            object value = ParseValue(cell, member.Type);
            member.Setter(obj, value);
        }

        return obj;
    }

    private static IEnumerable<MemberAccessor> BuildAccessors(bool includeNonPublic)
    {
        var t = typeof(T);
        var flags = BindingFlags.Instance | BindingFlags.Public;
        if (includeNonPublic) flags |= BindingFlags.NonPublic;

        foreach (var f in t.GetFields(flags))
            yield return MemberAccessor.ForField(f);

        foreach (var p in t.GetProperties(flags))
        {
            if (!p.CanRead || !p.CanWrite) continue;
            if (p.GetIndexParameters().Length != 0) continue;
            yield return MemberAccessor.ForProperty(p);
        }
    }

    private static string FormatValue(object value, Type type)
    {
        if (value == null) return "";

        if (type == typeof(string)) return (string)value;

        if (type == typeof(int[]))
        {
            var arr = (int[])value;
            return string.Join(";", arr);
        }

        if (type.IsEnum) return value.ToString() ?? "";

        var formattable = value as IFormattable;
        if (formattable != null)
            return formattable.ToString(null, CultureInfo.InvariantCulture);

        return value.ToString() ?? "";
    }

    private static object ParseValue(string text, Type type)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (type.IsValueType) return Activator.CreateInstance(type);
            return null;
        }

        if (type == typeof(string)) return text;
        if (type == typeof(int)) return int.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(double)) return double.Parse(text, CultureInfo.InvariantCulture);
        if (type == typeof(bool)) return bool.Parse(text);
        if (type.IsEnum) return Enum.Parse(type, text);

        if (type == typeof(int[]))
        {
            var parts = text.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var arr = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                arr[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
            return arr;
        }

        throw new NotSupportedException("Тип не поддерживается: " + type.FullName);
    }

    private static string EscapeCsv(string s)
    {
        if (s == null) return "";

        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";

        return s;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == ',')
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                }
                else if (c == '"')
                {
                    inQuotes = true;
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        result.Add(sb.ToString());
        return result;
    }

    private sealed class MemberAccessor
    {
        public string Name { get; private set; }
        public Type Type { get; private set; }
        public Func<T, object> Getter { get; private set; }
        public Action<T, object> Setter { get; private set; }

        private MemberAccessor(string name, Type type, Func<T, object> getter, Action<T, object> setter)
        {
            Name = name;
            Type = type;
            Getter = getter;
            Setter = setter;
        }

        public static MemberAccessor ForField(FieldInfo f)
        {
            var objParam = Expression.Parameter(typeof(T), "obj");
            var fieldExpr = Expression.Field(objParam, f);
            var box = Expression.Convert(fieldExpr, typeof(object));
            var getter = Expression.Lambda<Func<T, object>>(box, objParam).Compile();

            var valParam = Expression.Parameter(typeof(object), "val");
            var assign = Expression.Assign(fieldExpr, Expression.Convert(valParam, f.FieldType));
            var setter = Expression.Lambda<Action<T, object>>(assign, objParam, valParam).Compile();

            return new MemberAccessor(f.Name, f.FieldType, getter, setter);
        }

        public static MemberAccessor ForProperty(PropertyInfo p)
        {
            var objParam = Expression.Parameter(typeof(T), "obj");
            var propExpr = Expression.Property(objParam, p);
            var box = Expression.Convert(propExpr, typeof(object));
            var getter = Expression.Lambda<Func<T, object>>(box, objParam).Compile();

            var valParam = Expression.Parameter(typeof(object), "val");
            var assign = Expression.Assign(propExpr, Expression.Convert(valParam, p.PropertyType));
            var setter = Expression.Lambda<Action<T, object>>(assign, objParam, valParam).Compile();

            return new MemberAccessor(p.Name, p.PropertyType, getter, setter);
        }
    }
}

public sealed class PrivateFieldsContractResolver : DefaultContractResolver
{
    protected override IList<JsonProperty> CreateProperties(Type type, MemberSerialization memberSerialization)
    {
        var props = base.CreateProperties(type, memberSerialization);

        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var privateFields = type.GetFields(flags);

        foreach (var field in privateFields)
        {
            var jp = base.CreateProperty(field, memberSerialization);
            jp.Readable = true;
            jp.Writable = true;
            props.Add(jp);
        }

        return props;
    }
}


public static class Program
{
    public static void Main()
    {
        const int iterations = 1000;

        var obj = new F();
        var csv = new CsvReflectionSerializer<F>(includeNonPublic: true);

        var jsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new PrivateFieldsContractResolver()
        };

        csv.Serialize(obj);
        csv.Deserialize(csv.Serialize(obj));

        JsonConvert.SerializeObject(obj, jsonSettings);
        JsonConvert.DeserializeObject<F>(JsonConvert.SerializeObject(obj, jsonSettings), jsonSettings);

        ForceGC();

        string csvText = "";
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            csvText = csv.Serialize(obj, includeHeader: true);
        sw.Stop();
        long csvSerializeMs = sw.ElapsedMilliseconds;

        ForceGC();
        sw.Restart();
        Console.WriteLine("----- Пример CSV (последний результат) -----");
        Console.WriteLine(csvText);
        Console.WriteLine("----- Конец примера -----");
        sw.Stop();
        long consoleWriteMs = sw.ElapsedMilliseconds;

        ForceGC();
        sw.Restart();
        F objFromCsv = default(F);
        for (int i = 0; i < iterations; i++)
            objFromCsv = csv.Deserialize(csvText);
        sw.Stop();
        long csvDeserializeMs = sw.ElapsedMilliseconds;

        ForceGC();
        string jsonText = "";
        sw.Restart();
        for (int i = 0; i < iterations; i++)
            jsonText = JsonConvert.SerializeObject(obj, jsonSettings);
        sw.Stop();
        long jsonSerializeMs = sw.ElapsedMilliseconds;

        ForceGC();
        sw.Restart();
        F objFromJson = null;
        for (int i = 0; i < iterations; i++)
            objFromJson = JsonConvert.DeserializeObject<F>(jsonText, jsonSettings);
        sw.Stop();
        long jsonDeserializeMs = sw.ElapsedMilliseconds;

        Console.WriteLine();
        Console.WriteLine("Сериализуемый класс: class F");
        Console.WriteLine("{");
        Console.WriteLine("    int i1;");
        Console.WriteLine("    int i2;");
        Console.WriteLine("    int i3;");
        Console.WriteLine("    int i4;");
        Console.WriteLine("    int i5;");
        Console.WriteLine("    public int[] mas;");
        Console.WriteLine("    public F()");
        Console.WriteLine("    {");
        Console.WriteLine("        i1 = 1; i2 = 2; i3 = 3; i4 = 4; i5 = 5;");
        Console.WriteLine("        mas = new int[] { 1, 2 };");
        Console.WriteLine("    }");
        Console.WriteLine("    public F Get() => new F();");
        Console.WriteLine("}");
        Console.WriteLine();
        Console.WriteLine("код сериализации-десериализации: CsvReflectionSerializer (Reflection + Expression) + NewtonsoftJson (private fields)");
        Console.WriteLine();
        Console.WriteLine("количество замеров: " + iterations + " итераций");
        Console.WriteLine();
        Console.WriteLine("мой рефлекшен:");
        Console.WriteLine();
        Console.WriteLine("Время на сериализацию = " + csvSerializeMs + " мс");
        Console.WriteLine("Время на десериализацию = " + csvDeserializeMs + " мс");
        Console.WriteLine();
        Console.WriteLine("Время на вывод текста в консоль = " + consoleWriteMs + " мс");
        Console.WriteLine();
        Console.WriteLine("стандартный механизм (NewtonsoftJson):");
        Console.WriteLine();
        Console.WriteLine("Время на сериализацию = " + jsonSerializeMs + " мс");
        Console.WriteLine("Время на десериализацию = " + jsonDeserializeMs + " мс");
        Console.WriteLine();
        Console.WriteLine("Пример JSON: " + jsonText);
        Console.WriteLine();
        Console.WriteLine("Проверка (CSV): mas.Length=" + (objFromCsv.mas == null ? "null" : objFromCsv.mas.Length.ToString()));
        Console.WriteLine("Проверка (JSON): mas.Length=" + (objFromJson == null || objFromJson.mas == null ? "null" : objFromJson.mas.Length.ToString()));
    }

    private static void ForceGC()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
