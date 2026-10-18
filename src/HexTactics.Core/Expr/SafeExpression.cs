using System;
using System.Collections.Generic;
using System.Globalization;

namespace HexTactics.Core.Expr;

/// <summary>
/// Safe restricted math expression: numbers, whitelisted variables, + - * / ^, unary minus,
/// comparison (&lt; &gt; &lt;= &gt;= == !=, result 1/0), parentheses and whitelisted functions
/// (min, max, clamp, abs, floor, ceil, round, sqrt, if). No user code is ever executed:
/// the expression is parsed into a small AST and evaluated.
/// </summary>
public sealed class SafeExpression
{
    public const int MaxLength = 512;
    public const int MaxDepth = 32;

    private static readonly Dictionary<string, (int MinArgs, int MaxArgs)> Functions = new()
    {
        ["min"] = (2, 8),
        ["max"] = (2, 8),
        ["clamp"] = (3, 3),
        ["abs"] = (1, 1),
        ["floor"] = (1, 1),
        ["ceil"] = (1, 1),
        ["round"] = (1, 1),
        ["sqrt"] = (1, 1),
        ["if"] = (3, 3),
    };

    public static IReadOnlyCollection<string> FunctionNames => Functions.Keys;

    private readonly Node _root;

    private SafeExpression(string source, Node root, HashSet<string> vars)
    {
        Source = source;
        _root = root;
        Variables = vars;
    }

    public string Source { get; }
    public IReadOnlyCollection<string> Variables { get; }

    /// <summary>Parses the expression. allowedVariables == null allows any identifier.</summary>
    public static bool TryParse(string text, ICollection<string>? allowedVariables, out SafeExpression? expr, out string error)
    {
        expr = null;
        error = "";
        if (text == null || text.Trim().Length == 0)
        {
            error = "Пустое выражение";
            return false;
        }
        if (text.Length > MaxLength)
        {
            error = $"Выражение длиннее {MaxLength} символов";
            return false;
        }
        try
        {
            var p = new Parser(text, allowedVariables);
            var node = p.ParseAll();
            expr = new SafeExpression(text, node, p.UsedVariables);
            return true;
        }
        catch (FormatException e)
        {
            error = e.Message;
            return false;
        }
    }

    public static SafeExpression Parse(string text, ICollection<string>? allowedVariables = null)
    {
        if (!TryParse(text, allowedVariables, out var e, out var err)) throw new FormatException(err);
        return e!;
    }

    /// <summary>Evaluates; unknown variables evaluate to 0. Result NaN/∞ is replaced with 0.</summary>
    public double Evaluate(IReadOnlyDictionary<string, double> vars)
    {
        double v = _root.Eval(vars);
        return double.IsFinite(v) ? v : 0.0;
    }

    public double Evaluate(Func<string, double> resolver)
    {
        double v = _root.Eval(resolver);
        return double.IsFinite(v) ? v : 0.0;
    }

    // ---------------- AST ----------------

    private abstract class Node
    {
        public double Eval(IReadOnlyDictionary<string, double> vars) => Eval(n => vars.TryGetValue(n, out var x) ? x : 0.0);
        public abstract double Eval(Func<string, double> vars);
    }

    private sealed class Num : Node
    {
        private readonly double _v;
        public Num(double v) => _v = v;
        public override double Eval(Func<string, double> vars) => _v;
    }

    private sealed class Var : Node
    {
        private readonly string _name;
        public Var(string name) => _name = name;
        public override double Eval(Func<string, double> vars) => vars(_name);
    }

    private sealed class Unary : Node
    {
        private readonly Node _a;
        public Unary(Node a) => _a = a;
        public override double Eval(Func<string, double> vars) => -_a.Eval(vars);
    }

    private sealed class Bin : Node
    {
        private readonly string _op;
        private readonly Node _a, _b;

        public Bin(string op, Node a, Node b)
        {
            _op = op;
            _a = a;
            _b = b;
        }

        public override double Eval(Func<string, double> vars)
        {
            double a = _a.Eval(vars), b = _b.Eval(vars);
            return _op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                "/" => b == 0 ? 0 : a / b,
                "^" => Math.Pow(a, b),
                "<" => a < b ? 1 : 0,
                ">" => a > b ? 1 : 0,
                "<=" => a <= b ? 1 : 0,
                ">=" => a >= b ? 1 : 0,
                "==" => Math.Abs(a - b) < 1e-9 ? 1 : 0,
                "!=" => Math.Abs(a - b) >= 1e-9 ? 1 : 0,
                _ => 0,
            };
        }
    }

    private sealed class Call : Node
    {
        private readonly string _fn;
        private readonly List<Node> _args;

        public Call(string fn, List<Node> args)
        {
            _fn = fn;
            _args = args;
        }

        public override double Eval(Func<string, double> vars)
        {
            if (_fn == "if") return _args[0].Eval(vars) != 0 ? _args[1].Eval(vars) : _args[2].Eval(vars);
            var a = new double[_args.Count];
            for (int i = 0; i < a.Length; i++) a[i] = _args[i].Eval(vars);
            switch (_fn)
            {
                case "min":
                {
                    double m = a[0];
                    for (int i = 1; i < a.Length; i++) m = Math.Min(m, a[i]);
                    return m;
                }
                case "max":
                {
                    double m = a[0];
                    for (int i = 1; i < a.Length; i++) m = Math.Max(m, a[i]);
                    return m;
                }
                case "clamp": return a[1] > a[2] ? a[1] : Math.Clamp(a[0], a[1], a[2]);
                case "abs": return Math.Abs(a[0]);
                case "floor": return Math.Floor(a[0]);
                case "ceil": return Math.Ceiling(a[0]);
                case "round": return Math.Round(a[0], MidpointRounding.AwayFromZero);
                case "sqrt": return a[0] < 0 ? 0 : Math.Sqrt(a[0]);
                default: return 0;
            }
        }
    }

    // ---------------- Parser (recursive descent) ----------------

    private sealed class Parser
    {
        private readonly string _s;
        private readonly ICollection<string>? _allowed;
        private int _pos;
        private int _depth;

        public Parser(string s, ICollection<string>? allowed)
        {
            _s = s;
            _allowed = allowed;
        }

        public HashSet<string> UsedVariables { get; } = new();

        public Node ParseAll()
        {
            var n = ParseComparison();
            SkipWs();
            if (_pos < _s.Length) throw Err($"Неожиданный символ '{_s[_pos]}'");
            return n;
        }

        private FormatException Err(string msg) => new($"{msg} (позиция {_pos + 1})");

        private void SkipWs()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos])) _pos++;
        }

        private bool Match(string tok)
        {
            SkipWs();
            if (string.CompareOrdinal(_s, _pos, tok, 0, tok.Length) == 0)
            {
                _pos += tok.Length;
                return true;
            }
            return false;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw Err("Слишком глубокая вложенность");
        }

        private Node ParseComparison()
        {
            var left = ParseAdd();
            foreach (var op in new[] { "<=", ">=", "==", "!=", "<", ">" })
            {
                if (Match(op))
                {
                    var right = ParseAdd();
                    return new Bin(op, left, right);
                }
            }
            return left;
        }

        private Node ParseAdd()
        {
            var left = ParseMul();
            while (true)
            {
                if (Match("+")) left = new Bin("+", left, ParseMul());
                else if (Match("-")) left = new Bin("-", left, ParseMul());
                else return left;
            }
        }

        private Node ParseMul()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match("*")) left = new Bin("*", left, ParseUnary());
                else if (Match("/")) left = new Bin("/", left, ParseUnary());
                else return left;
            }
        }

        // Unary minus binds weaker than '^': -2^2 = -(2^2).
        private Node ParseUnary()
        {
            if (Match("-"))
            {
                Enter();
                var n = new Unary(ParseUnary());
                _depth--;
                return n;
            }
            if (Match("+")) return ParseUnary();
            return ParsePow();
        }

        private Node ParsePow()
        {
            var b = ParsePrimary();
            if (Match("^"))
            {
                Enter();
                var e = ParseUnary();
                _depth--;
                return new Bin("^", b, e);
            }
            return b;
        }

        private Node ParsePrimary()
        {
            SkipWs();
            if (_pos >= _s.Length) throw Err("Неожиданный конец выражения");
            char c = _s[_pos];
            if (c == '(')
            {
                _pos++;
                Enter();
                var n = ParseComparison();
                _depth--;
                if (!Match(")")) throw Err("Ожидалась ')'");
                return n;
            }
            if (char.IsDigit(c) || c == '.')
            {
                int start = _pos;
                while (_pos < _s.Length && (char.IsDigit(_s[_pos]) || _s[_pos] == '.')) _pos++;
                var txt = _s.Substring(start, _pos - start);
                if (!double.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw Err($"Некорректное число '{txt}'");
                return new Num(v);
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = _pos;
                while (_pos < _s.Length && (char.IsLetterOrDigit(_s[_pos]) || _s[_pos] == '_' || _s[_pos] == '.')) _pos++;
                var name = _s.Substring(start, _pos - start);
                SkipWs();
                if (_pos < _s.Length && _s[_pos] == '(')
                {
                    if (!Functions.TryGetValue(name, out var arity)) throw Err($"Функция '{name}' не разрешена");
                    _pos++;
                    Enter();
                    var args = new List<Node>();
                    if (!Match(")"))
                    {
                        do
                        {
                            args.Add(ParseComparison());
                        } while (Match(","));
                        if (!Match(")")) throw Err("Ожидалась ')' после аргументов");
                    }
                    _depth--;
                    if (args.Count < arity.MinArgs || args.Count > arity.MaxArgs)
                        throw Err($"Функция '{name}' принимает {arity.MinArgs}–{arity.MaxArgs} аргументов");
                    return new Call(name, args);
                }
                if (_allowed != null && !_allowed.Contains(name)) throw Err($"Переменная '{name}' не разрешена");
                UsedVariables.Add(name);
                return new Var(name);
            }
            throw Err($"Неожиданный символ '{c}'");
        }
    }
}
