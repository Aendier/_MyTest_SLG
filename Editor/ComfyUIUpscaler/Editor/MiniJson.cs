using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ComfyUIUpscaler.Editor
{
    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            return Parser.Parse(json);
        }

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }

        private sealed class Parser : IDisposable
        {
            private const string WordBreak = "{}[],:\"";
            private readonly StringReader reader;

            private Parser(string json)
            {
                reader = new StringReader(json);
            }

            public static object Parse(string json)
            {
                using (var parser = new Parser(json))
                    return parser.ParseValue();
            }

            public void Dispose()
            {
                reader.Dispose();
            }

            private Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>(StringComparer.Ordinal);
                reader.Read();
                while (true)
                {
                    Token token = NextToken;
                    if (token == Token.None)
                        return null;
                    if (token == Token.CurlyClose)
                    {
                        reader.Read();
                        return table;
                    }
                    if (token != Token.String)
                        return null;

                    string name = ParseString();
                    if (NextToken != Token.Colon)
                        return null;
                    reader.Read();
                    table[name] = ParseValue();

                    token = NextToken;
                    if (token == Token.Comma)
                    {
                        reader.Read();
                        continue;
                    }
                    if (token == Token.CurlyClose)
                    {
                        reader.Read();
                        return table;
                    }
                    return null;
                }
            }

            private List<object> ParseArray()
            {
                var array = new List<object>();
                reader.Read();
                bool parsing = true;
                while (parsing)
                {
                    Token token = NextToken;
                    switch (token)
                    {
                        case Token.None:
                            return null;
                        case Token.SquareClose:
                            reader.Read();
                            parsing = false;
                            break;
                        case Token.Comma:
                            reader.Read();
                            break;
                        default:
                            array.Add(ParseByToken(token));
                            break;
                    }
                }
                return array;
            }

            private object ParseValue()
            {
                return ParseByToken(NextToken);
            }

            private object ParseByToken(Token token)
            {
                switch (token)
                {
                    case Token.String:
                        return ParseString();
                    case Token.Number:
                        return ParseNumber();
                    case Token.CurlyOpen:
                        return ParseObject();
                    case Token.SquareOpen:
                        return ParseArray();
                    case Token.True:
                        return true;
                    case Token.False:
                        return false;
                    case Token.Null:
                        return null;
                    default:
                        return null;
                }
            }

            private string ParseString()
            {
                var builder = new StringBuilder();
                reader.Read();
                while (reader.Peek() != -1)
                {
                    char character = NextChar;
                    if (character == '"')
                        break;
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (reader.Peek() == -1)
                        break;
                    character = NextChar;
                    switch (character)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            var hex = new char[4];
                            // 畸形 JSON 可能在 \u 后不足 4 位，遇到 EOF 直接结束，避免 Convert.ToChar(-1) 抛 OverflowException
                            for (int i = 0; i < 4; i++)
                            {
                                if (reader.Peek() == -1)
                                    return builder.ToString();
                                hex[i] = NextChar;
                            }
                            builder.Append((char)Convert.ToInt32(new string(hex), 16));
                            break;
                    }
                }
                return builder.ToString();
            }

            private object ParseNumber()
            {
                string number = NextWord;
                if (number.IndexOf('.') < 0 && number.IndexOf('e') < 0 && number.IndexOf('E') < 0 &&
                    long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                    return integer;
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double floating))
                    return floating;
                return null;
            }

            private void EatWhitespace()
            {
                while (reader.Peek() != -1 && char.IsWhiteSpace(PeekChar))
                {
                    reader.Read();
                }
            }

            private char PeekChar => Convert.ToChar(reader.Peek());
            private char NextChar => Convert.ToChar(reader.Read());

            private string NextWord
            {
                get
                {
                    var builder = new StringBuilder();
                    while (reader.Peek() != -1 && !IsWordBreak(PeekChar))
                        builder.Append(NextChar);
                    return builder.ToString();
                }
            }

            private Token NextToken
            {
                get
                {
                    EatWhitespace();
                    if (reader.Peek() == -1)
                        return Token.None;
                    switch (PeekChar)
                    {
                        case '{': return Token.CurlyOpen;
                        case '}': return Token.CurlyClose;
                        case '[': return Token.SquareOpen;
                        case ']': return Token.SquareClose;
                        case ',': return Token.Comma;
                        case '"': return Token.String;
                        case ':': return Token.Colon;
                        case '0': case '1': case '2': case '3': case '4':
                        case '5': case '6': case '7': case '8': case '9': case '-':
                            return Token.Number;
                    }

                    string word = NextWord;
                    switch (word)
                    {
                        case "false": return Token.False;
                        case "true": return Token.True;
                        case "null": return Token.Null;
                        default: return Token.None;
                    }
                }
            }

            private static bool IsWordBreak(char character)
            {
                return char.IsWhiteSpace(character) || WordBreak.IndexOf(character) != -1;
            }

            private enum Token
            {
                None,
                CurlyOpen,
                CurlyClose,
                SquareOpen,
                SquareClose,
                Colon,
                Comma,
                String,
                Number,
                True,
                False,
                Null
            }
        }

        private sealed class Serializer
        {
            private readonly StringBuilder builder = new StringBuilder();

            public static string Serialize(object value)
            {
                var serializer = new Serializer();
                serializer.SerializeValue(value);
                return serializer.builder.ToString();
            }

            private void SerializeValue(object value)
            {
                if (value == null)
                {
                    builder.Append("null");
                }
                else if (value is string text)
                {
                    SerializeString(text);
                }
                else if (value is bool boolean)
                {
                    builder.Append(boolean ? "true" : "false");
                }
                else if (value is IDictionary dictionary)
                {
                    SerializeObject(dictionary);
                }
                else if (value is IList list)
                {
                    SerializeArray(list);
                }
                else if (value is char character)
                {
                    SerializeString(character.ToString());
                }
                else
                {
                    SerializeNumber(value);
                }
            }

            private void SerializeObject(IDictionary dictionary)
            {
                bool first = true;
                builder.Append('{');
                foreach (object key in dictionary.Keys)
                {
                    if (!first)
                        builder.Append(',');
                    SerializeString(key.ToString());
                    builder.Append(':');
                    SerializeValue(dictionary[key]);
                    first = false;
                }
                builder.Append('}');
            }

            private void SerializeArray(IList array)
            {
                builder.Append('[');
                bool first = true;
                foreach (object value in array)
                {
                    if (!first)
                        builder.Append(',');
                    SerializeValue(value);
                    first = false;
                }
                builder.Append(']');
            }

            private void SerializeString(string text)
            {
                builder.Append('"');
                foreach (char character in text)
                {
                    switch (character)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (character < 32 || character > 126)
                                builder.Append("\\u" + ((int)character).ToString("x4"));
                            else
                                builder.Append(character);
                            break;
                    }
                }
                builder.Append('"');
            }

            private void SerializeNumber(object number)
            {
                if (number is float single)
                    builder.Append(single.ToString("R", CultureInfo.InvariantCulture));
                else if (number is double dbl)
                    builder.Append(dbl.ToString("R", CultureInfo.InvariantCulture));
                else if (number is decimal dec)
                    builder.Append(dec.ToString(CultureInfo.InvariantCulture));
                else
                    builder.Append(Convert.ToString(number, CultureInfo.InvariantCulture));
            }
        }
    }
}
