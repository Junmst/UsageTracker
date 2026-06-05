using System;
using System.Linq;

namespace UsageTrackerNative;

public static class SearchExpressionMatcher
{
    public static bool IsMatch(string? expression, Func<string, bool> matchesTerm)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var parser = new Parser(expression, matchesTerm);
        return parser.Parse();
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly Func<string, bool> _matchesTerm;
        private int _position;

        public Parser(string text, Func<string, bool> matchesTerm)
        {
            _text = text;
            _matchesTerm = matchesTerm;
        }

        public bool Parse()
        {
            SkipWhiteSpace();
            if (IsEnd)
            {
                return true;
            }

            var result = ParseOr();
            SkipWhiteSpace();
            return result && IsEnd;
        }

        private bool ParseOr()
        {
            var result = ParseAnd();
            while (true)
            {
                SkipWhiteSpace();
                if (!Consume('|'))
                {
                    return result;
                }

                result = ParseAnd() || result;
            }
        }

        private bool ParseAnd()
        {
            var result = ParseNot();
            while (true)
            {
                SkipWhiteSpace();
                if (!Consume('&'))
                {
                    return result;
                }

                result = ParseNot() && result;
            }
        }

        private bool ParseNot()
        {
            SkipWhiteSpace();
            var negate = false;
            while (Consume('!') || Consume('！'))
            {
                negate = !negate;
                SkipWhiteSpace();
            }

            var value = ParsePrimary();
            return negate ? !value : value;
        }

        private bool ParsePrimary()
        {
            SkipWhiteSpace();
            if (Consume('(') || Consume('（'))
            {
                var value = ParseOr();
                SkipWhiteSpace();
                _ = Consume(')') || Consume('）');
                return value;
            }

            var term = ReadTerm();
            return !string.IsNullOrWhiteSpace(term) && _matchesTerm(term.Trim());
        }

        private string ReadTerm()
        {
            var start = _position;
            while (!IsEnd && !IsOperator(Current))
            {
                _position++;
            }

            return _text[start.._position];
        }

        private void SkipWhiteSpace()
        {
            while (!IsEnd && char.IsWhiteSpace(Current))
            {
                _position++;
            }
        }

        private bool Consume(char value)
        {
            if (IsEnd || Current != value)
            {
                return false;
            }

            _position++;
            return true;
        }

        private bool IsEnd => _position >= _text.Length;

        private char Current => _text[_position];

        private static bool IsOperator(char value)
        {
            return value is '&' or '|' or '!' or '！' or '(' or ')' or '（' or '）';
        }
    }
}
