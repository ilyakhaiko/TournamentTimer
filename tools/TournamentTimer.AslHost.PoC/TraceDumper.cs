using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using LiveSplit.Model;

internal static class TraceDumper
{
    private const int MaxItems = 30;
    private const int MaxDepth = 2;

    public static void DumpAslScript(object script, LiveSplitState liveSplitState, int tick)
    {
        Console.WriteLine($"[trace] tick={tick} phase={liveSplitState.CurrentPhase}, splitIndex={liveSplitState.CurrentSplitIndex}, split={liveSplitState.CurrentSplit?.Name ?? "-"}");
        DumpProperty(script, "GameVersion");
        DumpProperty(script, "RefreshRate");
        DumpProperty(script, "State");
        DumpProperty(script, "OldState");
        DumpProperty(script, "Vars");
    }

    private static void DumpProperty(object owner, string propertyName)
    {
        try
        {
            var property = owner.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
            {
                Console.WriteLine($"[trace] {propertyName}: <missing>");
                return;
            }

            var value = property.GetValue(owner, null);
            Console.WriteLine($"[trace] {propertyName}:");
            DumpValue(value, "  ", 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[trace] {propertyName}: <error: {ex.Message}>");
        }
    }

    private static void DumpValue(object value, string indent, int depth)
    {
        if (value == null)
        {
            Console.WriteLine(indent + "null");
            return;
        }

        var type = value.GetType();

        if (IsSimple(type))
        {
            Console.WriteLine(indent + FormatSimple(value));
            return;
        }

        if (depth >= MaxDepth)
        {
            Console.WriteLine(indent + FormatObjectHeader(value));
            return;
        }

        var dictionary = value as IDictionary;
        if (dictionary != null)
        {
            Console.WriteLine(indent + FormatObjectHeader(value));
            var count = 0;
            foreach (DictionaryEntry entry in dictionary)
            {
                if (count++ >= MaxItems)
                {
                    Console.WriteLine(indent + "  ...");
                    break;
                }

                Console.Write(indent + "  " + entry.Key + " = ");
                DumpInlineOrNested(entry.Value, indent + "  ", depth + 1);
            }

            return;
        }

        var enumerable = value as IEnumerable;
        if (!(value is string) && enumerable != null)
        {
            Console.WriteLine(indent + FormatObjectHeader(value));
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= MaxItems)
                {
                    Console.WriteLine(indent + "  ...");
                    break;
                }

                Console.Write(indent + "  [" + (count - 1) + "] = ");
                DumpInlineOrNested(item, indent + "  ", depth + 1);
            }

            return;
        }

        Console.WriteLine(indent + FormatObjectHeader(value));

        var members = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>()
            .Concat(type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            .Take(MaxItems)
            .ToArray();

        if (members.Length == 0)
        {
            return;
        }

        foreach (var member in members)
        {
            object memberValue;

            try
            {
                var property = member as PropertyInfo;
                if (property != null)
                {
                    memberValue = property.GetValue(value, null);
                }
                else
                {
                    memberValue = ((FieldInfo)member).GetValue(value);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(indent + "  " + member.Name + " = <error: " + ex.Message + ">");
                continue;
            }

            Console.Write(indent + "  " + member.Name + " = ");
            DumpInlineOrNested(memberValue, indent + "  ", depth + 1);
        }
    }

    private static void DumpInlineOrNested(object value, string indent, int depth)
    {
        if (value == null)
        {
            Console.WriteLine("null");
            return;
        }

        var type = value.GetType();
        if (IsSimple(type))
        {
            Console.WriteLine(FormatSimple(value));
            return;
        }

        Console.WriteLine();
        DumpValue(value, indent + "  ", depth);
    }

    private static bool IsSimple(Type type)
    {
        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(DateTimeOffset) ||
               type == typeof(TimeSpan);
    }

    private static string FormatSimple(object value)
    {
        var text = Convert.ToString(value) ?? "";
        if (text.Length > 180)
        {
            text = text.Substring(0, 180) + "...";
        }

        return text;
    }

    private static string FormatObjectHeader(object value)
    {
        return "<" + value.GetType().FullName + "> " + SafeToString(value);
    }

    private static string SafeToString(object value)
    {
        try
        {
            var text = value.ToString() ?? "";
            if (text == value.GetType().FullName)
            {
                return "";
            }

            if (text.Length > 180)
            {
                text = text.Substring(0, 180) + "...";
            }

            return text;
        }
        catch
        {
            return "";
        }
    }
}
