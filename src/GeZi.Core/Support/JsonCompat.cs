// GeZi —— 夸克网盘下载器 (Quark netdisk downloader)
// Copyright (C) 2026  Yi Yuan
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Text.Json;

namespace GeZi.Core.Support
{
    /// <summary>
    /// 泛型 JSON 对象图读写，形状与旧 JavaScriptSerializer 完全一致：
    /// 对象 → Dictionary&lt;string, object&gt;，数组 → object[]，
    /// 整数 → long，小数 → double，字符串/布尔/null 原样。
    /// 底层用框架自带的 System.Text.Json（零 NuGet）。
    /// </summary>
    public static class JsonCompat
    {
        private static readonly JsonDocumentOptions DocOpts = new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 128,
        };

        /// <summary>解析 JSON 为泛型对象图；失败抛 JsonException（调用方原本就按异常处理）。</summary>
        public static object Parse(string json)
        {
            using (var doc = JsonDocument.Parse(json, DocOpts))
                return FromElement(doc.RootElement);
        }

        /// <summary>按值的运行时类型序列化（字典/列表/基元均可）。</summary>
        public static string Serialize(object value)
        {
            if (value == null) return "null";
            return JsonSerializer.Serialize(value, value.GetType());
        }

        private static object FromElement(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var d = new Dictionary<string, object>();
                    foreach (var p in e.EnumerateObject())
                        d[p.Name] = FromElement(p.Value);
                    return d;
                case JsonValueKind.Array:
                    var arr = new object[e.GetArrayLength()];
                    int i = 0;
                    foreach (var el in e.EnumerateArray())
                        arr[i++] = FromElement(el);
                    return arr;
                case JsonValueKind.String:
                    return e.GetString();
                case JsonValueKind.Number:
                    return e.TryGetInt64(out var l) ? l : e.GetDouble();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                default:
                    return null;
            }
        }
    }
}
