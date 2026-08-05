using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class UpdJson
{
    public static UpdBox box(byte[] data)
    {
        JObject obj = readObj(data, UpdLim.LatestMax);
        fields(obj, "schema", "alg", "data", "sig");
        return new UpdBox
        {
            schema = intVal(obj, "schema"), alg = str(obj, "alg"),
            data = str(obj, "data"), sig = str(obj, "sig"),
        };
    }

    public static UpdLatest latest(byte[] data)
    {
        JObject obj = readObj(data, UpdLim.LatestMax);
        fields(obj, "schema", "env", "platform", "baseId", "seq", "releaseId",
            "manifestSha", "manifestSize");
        return new UpdLatest
        {
            schema = intVal(obj, "schema"), env = str(obj, "env"),
            platform = str(obj, "platform"), baseId = str(obj, "baseId"),
            seq = longVal(obj, "seq"), releaseId = str(obj, "releaseId"),
            manifestSha = str(obj, "manifestSha"), manifestSize = longVal(obj, "manifestSize"),
        };
    }

    public static UpdMan man(byte[] data)
    {
		JObject obj = readObj(data, UpdLim.ManMax);
		fields(obj, "schema", "env", "releaseId", "platform", "baseId",
			"aotDlls", "codeDlls", "entryDll", "hotId",
			"secret", "files");
        JArray list = arr(obj, "files");
        if (list.Count == 0 || list.Count > UpdLim.FileMax)
        {
            UpdFail.bad(UpdCode.Schema, "file_count");
        }
        UpdFile[] files = new UpdFile[list.Count];
        for (int i = 0; i < files.Length; ++i)
        {
            JObject item = list[i] as JObject;
            if (item == null)
            {
                UpdFail.bad(UpdCode.Schema, "file");
            }
            fields(item, "path", "sha256", "size");
            files[i] = new UpdFile
            {
                path = str(item, "path"), sha256 = str(item, "sha256"),
                size = longVal(item, "size"),
            };
        }
        return new UpdMan
        {
            schema = intVal(obj, "schema"), env = str(obj, "env"),
            releaseId = str(obj, "releaseId"),
			platform = str(obj, "platform"), baseId = str(obj, "baseId"),
            aotDlls = strList(obj, "aotDlls", 0, UpdLim.AotMax),
            codeDlls = strList(obj, "codeDlls", 2, UpdLim.CodeMax),
            entryDll = str(obj, "entryDll"),
            hotId = str(obj, "hotId"), secret = optStr(obj, "secret"), files = files,
        };
    }

    public static UpdState state(byte[] data)
    {
        JObject obj = readObj(data, UpdLim.StateMax);
        fields(obj, "schema", "env", "platform", "baseId", "seq", "releaseId", "latestSha");
        return new UpdState
        {
            schema = intVal(obj, "schema"), env = str(obj, "env"),
            platform = str(obj, "platform"), baseId = str(obj, "baseId"),
            seq = longVal(obj, "seq"), releaseId = str(obj, "releaseId"),
            latestSha = str(obj, "latestSha"),
        };
    }

    public static UpdActive active(byte[] data)
    {
        JObject obj = readObj(data, UpdLim.StateMax);
        return activeObj(obj);
    }

    public static UpdCandidate candidate(byte[] data)
    {
        JObject obj = readObj(data, UpdLim.StateMax);
        fields(obj, "schema", "target", "previous", "attempts");
        JObject target = obj["target"] as JObject;
        if (target == null)
        {
            UpdFail.bad(UpdCode.Schema, "target");
        }
        JToken previous = obj["previous"];
        if (previous == null ||
            previous.Type != JTokenType.Null && previous.Type != JTokenType.Object)
        {
            UpdFail.bad(UpdCode.Schema, "previous");
        }
        return new UpdCandidate
        {
            schema = intVal(obj, "schema"),
            target = activeObj(target),
            previous = previous.Type == JTokenType.Null
                ? null
                : activeObj((JObject)previous),
            attempts = intVal(obj, "attempts"),
        };
    }

    private static UpdActive activeObj(JObject obj)
    {
        fields(obj, "schema", "seq", "releaseId", "manifestSha", "manifestSize",
            "latestSha");
        return new UpdActive
        {
            schema = intVal(obj, "schema"), seq = longVal(obj, "seq"),
            releaseId = str(obj, "releaseId"), manifestSha = str(obj, "manifestSha"),
            manifestSize = longVal(obj, "manifestSize"), latestSha = str(obj, "latestSha"),
        };
    }

    private static JObject readObj(byte[] data, int max)
    {
        if (data == null || data.Length == 0 || data.Length > max)
        {
            UpdFail.bad(UpdCode.Schema, "json_size");
        }
        try
        {
            string json = UpdFmt.text(data);
            rejectCmt(json);
            using (StringReader input = new StringReader(json))
            using (JsonTextReader reader = makeReader(input))
            {
                JToken token = JToken.ReadFrom(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                    CommentHandling = CommentHandling.Load,
                    LineInfoHandling = LineInfoHandling.Ignore,
                });
                JObject obj = token as JObject;
                if (reader.Read() || obj == null)
                {
                    UpdFail.bad(UpdCode.Schema, "json_root");
                }
                return obj;
            }
        }
        catch (UpdBad)
        {
            throw;
        }
        catch (Exception ex)
        {
            UpdFail.bad(UpdCode.Schema, "json", UpdPhase.Idle, ex);
            return null;
        }
    }

    private static void rejectCmt(string json)
    {
        using (StringReader input = new StringReader(json))
        using (JsonTextReader reader = makeReader(input))
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonToken.Comment)
                {
                    UpdFail.bad(UpdCode.Schema, "json_comment");
                }
            }
        }
    }

    private static JsonTextReader makeReader(TextReader input)
    {
        return new JsonTextReader(input)
        {
            DateParseHandling = DateParseHandling.None,
            MaxDepth = 16,
        };
    }

    private static void fields(JObject obj, params string[] names)
    {
        if (obj.Count != names.Length)
        {
            UpdFail.bad(UpdCode.Schema, "fields");
        }
        for (int i = 0; i < names.Length; ++i)
        {
            if (obj.Property(names[i], StringComparison.Ordinal) == null)
            {
                UpdFail.bad(UpdCode.Schema, names[i]);
            }
        }
    }

    private static string str(JObject obj, string name)
    {
        JToken token = obj[name];
        if (token == null || token.Type != JTokenType.String || string.IsNullOrEmpty((string)token))
        {
            UpdFail.bad(UpdCode.Schema, name);
        }
        return (string)token;
    }

    private static string optStr(JObject obj, string name)
    {
        JToken token = obj[name];
        if (token == null || token.Type != JTokenType.String)
        {
            UpdFail.bad(UpdCode.Schema, name);
        }
        return (string)token;
    }

    private static int intVal(JObject obj, string name)
    {
        long value = longVal(obj, name);
        if (value < int.MinValue || value > int.MaxValue)
        {
            UpdFail.bad(UpdCode.Schema, name);
        }
        return (int)value;
    }

    private static long longVal(JObject obj, string name)
    {
        JToken token = obj[name];
        if (token == null || token.Type != JTokenType.Integer)
        {
            UpdFail.bad(UpdCode.Schema, name);
        }
        try
        {
            return token.Value<long>();
        }
        catch (Exception ex)
        {
            UpdFail.bad(UpdCode.Schema, name, UpdPhase.Idle, ex);
            return 0;
        }
    }

    private static JArray arr(JObject obj, string name)
    {
        JArray value = obj[name] as JArray;
        if (value == null)
        {
            UpdFail.bad(UpdCode.Schema, name);
        }
        return value;
    }

    private static string[] strList(JObject obj, string name, int min, int max)
    {
        JArray list = arr(obj, name);
        if (list.Count < min || list.Count > max)
        {
            UpdFail.bad(UpdCode.Schema, name);
        }
        string[] values = new string[list.Count];
        for (int i = 0; i < values.Length; ++i)
        {
            JToken token = list[i];
            if (token == null || token.Type != JTokenType.String ||
                string.IsNullOrEmpty((string)token))
            {
                UpdFail.bad(UpdCode.Schema, name);
            }
            values[i] = (string)token;
        }
        return values;
    }
}
