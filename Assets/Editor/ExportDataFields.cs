using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 把本工程的字段表（含中文别名）导出成数据端可直接消费的新格式：
/// {
///   "fields": {
///     "heading": { "column": "heading", "comment": "航向角(度)、航向、航向角" }
///   }
/// }
/// 数据端按 column 取值、按 comment 在配置界面显示中文列名。
/// </summary>
public static class ExportDataFields
{
    private const string MappingFile = "Assets/StreamingAssets/FieldKeyMapping.json";
    private const string OutputFile = "Assets/StreamingAssets/DataFields.json";

    [MenuItem("Tools/HK132/导出字段表（新格式）")]
    public static void Export()
    {
        if (!File.Exists(MappingFile))
        {
            EditorUtility.DisplayDialog("导出字段表", $"找不到 {MappingFile}", "好");
            return;
        }

        JObject root;
        try
        {
            root = JObject.Parse(File.ReadAllText(MappingFile, Encoding.UTF8));
        }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("导出字段表", $"解析 {MappingFile} 失败：{ex.Message}", "好");
            return;
        }

        var fields = new JObject();
        foreach (var property in root.Properties())
        {
            if (!(property.Value is JObject entry))
                continue;

            string column = (entry.Value<string>("internalKey") ?? property.Name ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(column))
                column = property.Name;

            var aliases = new List<string>();
            if (entry["aliases"] is JArray array)
            {
                foreach (var item in array)
                {
                    string alias = item?.Value<string>();
                    if (!string.IsNullOrWhiteSpace(alias))
                        aliases.Add(alias.Trim());
                }
            }

            fields[column] = new JObject
            {
                ["column"] = column,
                ["comment"] = BuildComment(aliases, column),
            };
        }

        var payload = new JObject { ["fields"] = fields };
        Directory.CreateDirectory(Path.GetDirectoryName(OutputFile) ?? "Assets/StreamingAssets");
        File.WriteAllText(OutputFile, payload.ToString(Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        AssetDatabase.Refresh();

        Debug.Log($"[ExportDataFields] 已导出 {fields.Count} 个字段到 {OutputFile}");
        EditorUtility.DisplayDialog("导出字段表", $"已导出 {fields.Count} 个字段：\n{OutputFile}", "好");
    }

    /// <summary>优先只保留中文别名（用顿号连接）；没有中文时退回全部别名，再退回列名。</summary>
    private static string BuildComment(List<string> aliases, string column)
    {
        var chinese = new List<string>();
        foreach (var alias in aliases)
        {
            if (ContainsChinese(alias))
                chinese.Add(alias);
        }

        var source = chinese.Count > 0 ? chinese : aliases;
        if (source.Count == 0)
            return column;

        return string.Join("、", source);
    }

    private static bool ContainsChinese(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 0x4E00 && c <= 0x9FFF)
                return true;
        }
        return false;
    }
}
