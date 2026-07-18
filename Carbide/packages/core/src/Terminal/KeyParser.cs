// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// T2 — adapted port of .NET 10.0.0's `System.Console/src/System/IO/KeyParser.cs`.
// Upstream: https://github.com/dotnet/runtime/blob/60629d14374c56f1cb51819049ad1fa529307f8d/src/libraries/System.Console/src/System/IO/KeyParser.cs (MIT).
//
// Surgery:
//   - The `TerminalFormatStrings` dependency is replaced with the minimal Carbide-owned
//     <see cref="XtermTerminfo"/> shim (xterm-256color only, IsRxvtTerm=false).
//   - `posixDisableValue` and `veraseCharacter` are hard-coded by the Carbide caller
//     (`CarbideConsole.ReadKeyAsync`) to `0xFF` (disabled) / `0x7F` (DEL) — browser has
//     no termios to query.
//   - Signature otherwise byte-compatible with upstream so a future resync is a clean merge.

using System.Collections.Generic;
using System.Diagnostics;

namespace Carbide.Terminal;

internal static class KeyParser
{
    private const char Escape = '\e';
    private const char Delete = '\u007F';
    private const char VtSequenceEndTag = '~';
    private const char ModifierSeparator = ';';
    private const int MinimalSequenceLength = 3;
    private const int SequencePrefixLength = 2; // ^[[ ("^[" stands for Escape)

    internal static ConsoleKeyInfo Parse(char[] buffer, XtermTerminfo terminalFormatStrings, byte posixDisableValue, byte veraseCharacter, ref int startIndex, int endIndex)
    {
        int length = endIndex - startIndex;
        Debug.Assert(length > 0);

        // VERASE overrides anything from Terminfo. Both settings can be different for Linux and macOS.
        if (buffer[startIndex] != posixDisableValue && buffer[startIndex] == veraseCharacter)
        {
            // the original char is preserved on purpose (backward compat + consistency)
            return new ConsoleKeyInfo(buffer[startIndex++], ConsoleKey.Backspace, false, false, false);
        }

        // Escape Sequences start with Escape. But some terminals like PuTTY and rxvt prepend Escape to express that for given sequence Alt was pressed.
        if (length >= MinimalSequenceLength + 1 && buffer[startIndex] == Escape && buffer[startIndex + 1] == Escape)
        {
            startIndex++;
            if (TryParseTerminalInputSequence(buffer, terminalFormatStrings, out ConsoleKeyInfo parsed, ref startIndex, endIndex))
            {
                return new ConsoleKeyInfo(parsed.KeyChar, parsed.Key, (parsed.Modifiers & ConsoleModifiers.Shift) != 0, alt: true, (parsed.Modifiers & ConsoleModifiers.Control) != 0);
            }
            startIndex--;
        }
        else if (length >= MinimalSequenceLength && TryParseTerminalInputSequence(buffer, terminalFormatStrings, out ConsoleKeyInfo parsed, ref startIndex, endIndex))
        {
            return parsed;
        }

        if (length == 2 && buffer[startIndex] == Escape && buffer[startIndex + 1] != Escape)
        {
            startIndex++; // skip the Escape
            return ParseFromSingleChar(buffer[startIndex++], isAlt: true);
        }

        return ParseFromSingleChar(buffer[startIndex++], isAlt: false);
    }

    private static bool TryParseTerminalInputSequence(char[] buffer, XtermTerminfo terminalFormatStrings, out ConsoleKeyInfo parsed, ref int startIndex, int endIndex)
    {
        ReadOnlySpan<char> input = buffer.AsSpan(startIndex, endIndex - startIndex);
        parsed = default;

        // sequences start with either "^[[" or "^[O". "^[" stands for Escape (27).
        if (input.Length < MinimalSequenceLength || input[0] != Escape || (input[1] != '[' && input[1] != 'O'))
        {
            return false;
        }

        Dictionary<string, ConsoleKeyInfo>.AlternateLookup<ReadOnlySpan<char>> terminfoDb =
            terminalFormatStrings.KeyFormatToConsoleKey.GetAlternateLookup<ReadOnlySpan<char>>();
        ConsoleModifiers modifiers = ConsoleModifiers.None;
        ConsoleKey key;

        // Is it a three character sequence? (examples: '^[[H' (Home), '^[OP' (F1))
        if (input[1] == 'O' || char.IsAsciiLetter(input[2]) || input.Length == MinimalSequenceLength)
        {
            if (!terminfoDb.TryGetValue(buffer.AsSpan(startIndex, MinimalSequenceLength), out parsed))
            {
                (key, modifiers) = input[1] == 'O' || terminalFormatStrings.IsRxvtTerm
                    ? MapKeyIdOXterm(input[2], terminalFormatStrings.IsRxvtTerm)
                    : MapSCO(input[2]);

                if (key == default)
                {
                    return false;
                }

                char keyChar = key switch
                {
                    ConsoleKey.Enter => '\r',
                    ConsoleKey.Add => '+',
                    ConsoleKey.Subtract => '-',
                    ConsoleKey.Divide => '/',
                    ConsoleKey.Multiply => '*',
                    _ => default
                };
                parsed = Create(keyChar, key, modifiers);
            }

            startIndex += MinimalSequenceLength;
            return true;
        }

        // Four-character sequence used by Linux Console / PuTTY emulation.
        if (input[1] == '[' && input[2] == '[' && char.IsBetween(input[3], 'A', 'E'))
        {
            if (!terminfoDb.TryGetValue(buffer.AsSpan(startIndex, 4), out parsed))
            {
                parsed = new ConsoleKeyInfo(default, ConsoleKey.F1 + input[3] - 'A', false, false, false);
            }

            startIndex += 4;
            return true;
        }

        int digitCount = !char.IsBetween(input[SequencePrefixLength], '1', '9')
            ? 0
            : char.IsDigit(input[SequencePrefixLength + 1]) ? 2 : 1;

        if (digitCount == 0
            || SequencePrefixLength + digitCount >= input.Length)
        {
            parsed = default;
            return false;
        }

        if (IsSequenceEndTag(input[SequencePrefixLength + digitCount]))
        {
            int sequenceLength = SequencePrefixLength + digitCount + 1;
            if (!terminfoDb.TryGetValue(buffer.AsSpan(startIndex, sequenceLength), out parsed))
            {
                key = MapEscapeSequenceNumber(byte.Parse(input.Slice(SequencePrefixLength, digitCount)));
                if (key == default)
                {
                    return false;
                }

                if (IsRxvtModifier(input[SequencePrefixLength + digitCount]))
                {
                    modifiers = MapRxvtModifiers(input[SequencePrefixLength + digitCount]);
                }

                parsed = Create(default, key, modifiers);
            }

            startIndex += sequenceLength;
            return true;
        }

        if (input[SequencePrefixLength + digitCount] is not ModifierSeparator
            || SequencePrefixLength + digitCount + 2 >= input.Length
            || !char.IsBetween(input[SequencePrefixLength + digitCount + 1], '2', '8')
            || (!char.IsAsciiLetterUpper(input[SequencePrefixLength + digitCount + 2]) && input[SequencePrefixLength + digitCount + 2] is not VtSequenceEndTag))
        {
            return false;
        }

        modifiers = MapXtermModifiers(input[SequencePrefixLength + digitCount + 1]);

        key = input[SequencePrefixLength + digitCount + 2] is VtSequenceEndTag
            ? MapEscapeSequenceNumber(byte.Parse(input.Slice(SequencePrefixLength, digitCount)))
            : MapKeyIdOXterm(input[SequencePrefixLength + digitCount + 2], terminalFormatStrings.IsRxvtTerm).key;

        if (key == default)
        {
            return false;
        }

        startIndex += SequencePrefixLength + digitCount + 3;
        parsed = Create(default, key, modifiers);
        return true;

        static (ConsoleKey key, ConsoleModifiers modifiers) MapKeyIdOXterm(char character, bool isRxvt)
            => character switch
            {
                'A' or 'x' => (ConsoleKey.UpArrow, 0),
                'a' => (ConsoleKey.UpArrow, ConsoleModifiers.Shift),
                'B' or 'r' => (ConsoleKey.DownArrow, 0),
                'b' => (ConsoleKey.DownArrow, ConsoleModifiers.Shift),
                'C' or 'v' => (ConsoleKey.RightArrow, 0),
                'c' => (ConsoleKey.RightArrow, ConsoleModifiers.Shift),
                'D' or 't' => (ConsoleKey.LeftArrow, 0),
                'd' => (ConsoleKey.LeftArrow, ConsoleModifiers.Shift),
                'E' => (ConsoleKey.NoName, 0),
                'F' or 'q' => (ConsoleKey.End, 0),
                'H' => (ConsoleKey.Home, 0),
                'j' => (ConsoleKey.Multiply, 0),
                'k' => (ConsoleKey.Add, 0),
                'm' => (ConsoleKey.Subtract, 0),
                'M' => (ConsoleKey.Enter, 0),
                'n' => (ConsoleKey.Delete, 0),
                'o' => (ConsoleKey.Divide, 0),
                'P' => (ConsoleKey.F1, 0),
                'p' => (ConsoleKey.Insert, 0),
                'Q' => (ConsoleKey.F2, 0),
                'R' => (ConsoleKey.F3, 0),
                'S' => (ConsoleKey.F4, 0),
                's' => (ConsoleKey.PageDown, 0),
                'T' => (ConsoleKey.F5, 0),
                'U' => (ConsoleKey.F6, 0),
                'u' => (ConsoleKey.NoName, 0),
                'V' => (ConsoleKey.F7, 0),
                'W' => (ConsoleKey.F8, 0),
                'w' when isRxvt => (ConsoleKey.Home, 0),
                'w' when !isRxvt => (ConsoleKey.End, 0),
                'X' => (ConsoleKey.F9, 0),
                'Y' => (ConsoleKey.F10, 0),
                'y' => (ConsoleKey.PageUp, 0),
                'Z' => (ConsoleKey.F11, 0),
                '[' => (ConsoleKey.F12, 0),
                _ => default
            };

        static (ConsoleKey key, ConsoleModifiers modifiers) MapSCO(char character)
            => character switch
            {
                'A' => (ConsoleKey.UpArrow, 0),
                'B' => (ConsoleKey.DownArrow, 0),
                'C' => (ConsoleKey.RightArrow, 0),
                'D' => (ConsoleKey.LeftArrow, 0),
                'F' => (ConsoleKey.End, 0),
                'G' => (ConsoleKey.PageDown, 0),
                'H' => (ConsoleKey.Home, 0),
                'I' => (ConsoleKey.PageUp, 0),
                _ when char.IsBetween(character, 'M', 'X') => (ConsoleKey.F1 + character - 'M', 0),
                _ when char.IsBetween(character, 'Y', 'Z') => (ConsoleKey.F1 + character - 'Y', ConsoleModifiers.Shift),
                _ when char.IsBetween(character, 'a', 'j') => (ConsoleKey.F3 + character - 'a', ConsoleModifiers.Shift),
                _ when char.IsBetween(character, 'k', 'v') => (ConsoleKey.F1 + character - 'k', ConsoleModifiers.Control),
                _ when char.IsBetween(character, 'w', 'z') => (ConsoleKey.F1 + character - 'w', ConsoleModifiers.Control | ConsoleModifiers.Shift),
                '@' => (ConsoleKey.F5, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                '[' => (ConsoleKey.F6, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                '<' or '\\' => (ConsoleKey.F7, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                ']' => (ConsoleKey.F8, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                '^' => (ConsoleKey.F9, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                '_' => (ConsoleKey.F10, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                '`' => (ConsoleKey.F11, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                '{' => (ConsoleKey.F12, ConsoleModifiers.Control | ConsoleModifiers.Shift),
                _ => default
            };

        static ConsoleKey MapEscapeSequenceNumber(byte number)
            => number switch
            {
                1 or 7 => ConsoleKey.Home,
                2 => ConsoleKey.Insert,
                3 => ConsoleKey.Delete,
                4 or 8 => ConsoleKey.End,
                5 => ConsoleKey.PageUp,
                6 => ConsoleKey.PageDown,
                11 => ConsoleKey.F1,
                12 => ConsoleKey.F2,
                13 => ConsoleKey.F3,
                14 => ConsoleKey.F4,
                15 => ConsoleKey.F5,
                17 => ConsoleKey.F6,
                18 => ConsoleKey.F7,
                19 => ConsoleKey.F8,
                20 => ConsoleKey.F9,
                21 => ConsoleKey.F10,
                23 => ConsoleKey.F11,
                24 => ConsoleKey.F12,
                25 => ConsoleKey.F13,
                26 => ConsoleKey.F14,
                28 => ConsoleKey.F15,
                29 => ConsoleKey.F16,
                31 => ConsoleKey.F17,
                32 => ConsoleKey.F18,
                33 => ConsoleKey.F19,
                34 => ConsoleKey.F20,
                _ => default
            };

        static ConsoleModifiers MapXtermModifiers(char modifier)
            => modifier switch
            {
                '2' => ConsoleModifiers.Shift,
                '3' => ConsoleModifiers.Alt,
                '4' => ConsoleModifiers.Shift | ConsoleModifiers.Alt,
                '5' => ConsoleModifiers.Control,
                '6' => ConsoleModifiers.Shift | ConsoleModifiers.Control,
                '7' => ConsoleModifiers.Alt | ConsoleModifiers.Control,
                '8' => ConsoleModifiers.Shift | ConsoleModifiers.Alt | ConsoleModifiers.Control,
                _ => default
            };

        static bool IsSequenceEndTag(char character) => character is VtSequenceEndTag || IsRxvtModifier(character);

        static bool IsRxvtModifier(char character) => MapRxvtModifiers(character) != default;

        static ConsoleModifiers MapRxvtModifiers(char modifier)
            => modifier switch
            {
                '^' => ConsoleModifiers.Control,
                '$' => ConsoleModifiers.Shift,
                '@' => ConsoleModifiers.Control | ConsoleModifiers.Shift,
                _ => default
            };

        static ConsoleKeyInfo Create(char keyChar, ConsoleKey key, ConsoleModifiers modifiers)
            => new(keyChar, key, (modifiers & ConsoleModifiers.Shift) != 0, (modifiers & ConsoleModifiers.Alt) != 0, (modifiers & ConsoleModifiers.Control) != 0);
    }

    private static ConsoleKeyInfo ParseFromSingleChar(char single, bool isAlt)
    {
        bool isShift = false, isCtrl = false;
        char keyChar = single;

        ConsoleKey key = single switch
        {
            '\b' => ConsoleKey.Backspace,
            '\t' => ConsoleKey.Tab,
            '\r' or '\n' => ConsoleKey.Enter,
            ' ' => ConsoleKey.Spacebar,
            Escape => ConsoleKey.Escape,
            Delete => ConsoleKey.Backspace,
            '*' => ConsoleKey.Multiply,
            '/' => ConsoleKey.Divide,
            '-' => ConsoleKey.Subtract,
            '+' => ConsoleKey.Add,
            '=' => default,
            '!' or '@' or '#' or '$' or '%' or '^' or '&' or '*' or '(' or ')' => default,
            ',' => ConsoleKey.OemComma,
            '.' => ConsoleKey.OemPeriod,
            _ when char.IsAsciiLetterLower(single) => ConsoleKey.A + single - 'a',
            _ when char.IsAsciiLetterUpper(single) => UppercaseCharacter(single, out isShift),
            _ when char.IsAsciiDigit(single) => ConsoleKey.D0 + single - '0',
            _ when char.IsBetween(single, (char)1, (char)26) => ControlAndLetterPressed(single, isAlt, out keyChar, out isCtrl),
            _ when char.IsBetween(single, (char)28, (char)31) => ControlAndDigitPressed(single, out keyChar, out isCtrl),
            '\u0000' => ControlAndDigitPressed(single, out keyChar, out isCtrl),
            _ => default
        };

        if (single is '\b' or '\n')
        {
            isCtrl = true;
        }

        if (isAlt)
        {
            isAlt = key != default;
        }

        return new ConsoleKeyInfo(keyChar, key, isShift, isAlt, isCtrl);

        static ConsoleKey UppercaseCharacter(char single, out bool isShift)
        {
            isShift = true;
            return ConsoleKey.A + single - 'A';
        }

        static ConsoleKey ControlAndLetterPressed(char single, bool isAlt, out char keyChar, out bool isCtrl)
        {
            Debug.Assert(single != 'b' && single != '\t' && single != '\n' && single != '\r');

            isCtrl = true;
            keyChar = isAlt ? default : single;
            return ConsoleKey.A + single - 1;
        }

        static ConsoleKey ControlAndDigitPressed(char single, out char keyChar, out bool isCtrl)
        {
            Debug.Assert(single == default || char.IsBetween(single, (char)28, (char)31));

            isCtrl = true;
            keyChar = default;
            return single switch
            {
                '\u0000' => ConsoleKey.D2,
                _ => ConsoleKey.D4 + single - 28
            };
        }
    }
}
