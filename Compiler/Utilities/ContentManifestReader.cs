using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace JSIL.Utilities {
    public class ContentManifestReader : IDisposable {
        public static readonly Regex EntryPrelude = new Regex(
            @"contentManifest\[(?'filename'.*)\] = \[",
            RegexOptions.Compiled
        );

        private readonly JavaScriptSerializer Serializer;
        private readonly StreamReader Input;
        private bool Disposed = false;

        public ContentManifestReader (StreamReader input) {
            Serializer = new JavaScriptSerializer();
            Input = input;

            if (!SeekToEntries())
                throw new InvalidDataException("Input stream does not contain a valid content manifest");
        }

        protected bool SeekToEntries () {
            string line;

            while ((line = Input.ReadLine()) != null) {
                if (EntryPrelude.IsMatch(line))
                    return true;
            }

            return false;
        }

        public IEnumerable<ContentManifestEntry> ReadEntries () {
            string line;

            while ((line = Input.ReadLine()) != null) {
                line = line.Trim();

                // HACK: Assume the manifest ends here.
                if (line.StartsWith("]"))
                    break;

                // HACK: We want to strip off the  trailing comma to make it a valid json fragment
                if (line.EndsWith(","))
                    line = line.Substring(0, line.Length - 1);

                var parsed = Serializer.Deserialize<object[]>(line);
                if ((parsed == null) || (parsed.Length != 3))
                    throw new InvalidDataException(line);

                yield return new ContentManifestEntry(
                    (string)parsed[0], (string)parsed[1],
                    (Dictionary<string, object>)parsed[2]
                );
            }
        }

        public void Dispose () {
            if (Disposed)
                return;

            Disposed = true;
            Input.Dispose();
        }
    }

    public struct ContentManifestEntry {
        public readonly string Type;
        public readonly string Path;
        public readonly Dictionary<string, object> Properties;

        public ContentManifestEntry (string type, string path, Dictionary<string, object> properties) {
            Type = type;
            Path = path;
            Properties = properties;
        }
    }
}
