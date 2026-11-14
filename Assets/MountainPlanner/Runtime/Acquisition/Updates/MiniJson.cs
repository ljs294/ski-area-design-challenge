#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MountainPlanner.Acquisition.Updates
{
    /// <summary>
    /// A small JSON reader for GitHub's release answer (task 4.23): objects become dictionaries, arrays lists, numbers
    /// doubles. Hand-written, with no reflection or package, so the launcher stays one exe with no DLLs beside it and the
    /// game reads it the same way. Throws <see cref="FormatException"/> on anything that isn't JSON.
    /// </summary>
    public static class MiniJson
    {
        const int MaxDepth = 64;

        public static object? Parse(string text)
        {
            int i = 0;
            object? value = Value(text, ref i, 0);
            Skip(text, ref i);
            if (i != text.Length) throw Error(i, "text after the value");
            return value;
        }

        static FormatException Error(int at, string what) => new FormatException($"Bad JSON at {at}: {what}.");

        static void Skip(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
        }

        static object? Value(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw Error(i, "nested too deep");
            Skip(s, ref i);
            if (i >= s.Length) throw Error(i, "unexpected end");
            char c = s[i];
            if (c == '{') return Obj(s, ref i, depth);
            if (c == '[') return Arr(s, ref i, depth);
            if (c == '"') return Str(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            return Num(s, ref i);
        }

        static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static Dictionary<string, object?> Obj(string s, ref int i, int depth)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (true)
            {
                Skip(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Error(i, "expected a name");
                string key = Str(s, ref i);
                Skip(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Error(i, "expected ':'");
                i++;
                map[key] = Value(s, ref i, depth + 1);
                Skip(s, ref i);
                if (i >= s.Length) throw Error(i, "unexpected end");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return map; }
                throw Error(i, "expected ',' or '}'");
            }
        }

        static List<object?> Arr(string s, ref int i, int depth)
        {
            var list = new List<object?>();
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                list.Add(Value(s, ref i, depth + 1));
                Skip(s, ref i);
                if (i >= s.Length) throw Error(i, "unexpected end");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw Error(i, "expected ',' or ']'");
            }
        }

        static string Str(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (true)
            {
                if (i >= s.Length) throw Error(i, "unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw Error(i, "unterminated escape");
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Error(i, "short \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw Error(i, "unknown escape");
                }
            }
        }

        static double Num(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start) throw Error(i, "unexpected character");
            if (!double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw Error(start, "bad number");
            return d;
        }
    }
}
