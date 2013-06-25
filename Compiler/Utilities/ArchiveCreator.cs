using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using JSIL.Compiler;
using JSIL.Compiler.Utilities;

namespace JSIL.Utilities {
    public class ArchiveCreator {
        public static readonly string SevenZipPath;
        public static readonly bool Available;

        static ArchiveCreator () {
            var assemblyPath = JSIL.Internal.Util.GetPathOfAssembly(Assembly.GetExecutingAssembly());
            SevenZipPath = Path.Combine(Path.GetDirectoryName(assemblyPath), @"..\Upstream\7Zip\7za.exe");
            Available = File.Exists(SevenZipPath);
        }

        // FIXME: 7za is such a pain about this file format that I should probably just write my own TAR packer...
        private static bool Invoke7Z (string arguments, string workingDirectory) {
            string stderr;
            byte[] stdout;

            var exitCode = IOUtil.Run(
                SevenZipPath,
                arguments,
                null, out stderr, out stdout,
                workingDirectory: workingDirectory
            );

            if (exitCode == 0) {
                return true;
            } else {
                Console.WriteLine("failed.");
                Console.WriteLine(Encoding.UTF8.GetString(stdout));
                Console.WriteLine(stderr);
                return false;
            }
        }

        public static bool CreateArchiveFromManifest (VariableSet variables, Configuration configuration, string manifestPath) {
            var manifestDirectory = Path.GetDirectoryName(manifestPath);
            var manifestName = Path.GetFileName(manifestPath).Replace(".manifest.js", "");

            if (configuration.ArchiveCreator.ExcludeManifests.Contains(manifestName)) {
                Console.WriteLine("// Skipped generating archive for '{0}'.", manifestName);
                return false;
            }

            var archiveFormat = configuration.ArchiveCreator.Format;
            var archiveName = String.Format("{0}.{1}", manifestName, archiveFormat);
            var archivePath = Path.Combine(manifestDirectory, archiveName);

            Console.Write("// Creating '{0}'... ", Path.GetFileName(archivePath));
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            if (File.Exists(archivePath + ".header"))
                File.Delete(archivePath + ".header");
            if (File.Exists(archivePath + ".footer"))
                File.Delete(archivePath + ".footer");

            var filenames = new List<string>();
            using (var reader = new ContentManifestReader(File.OpenText(manifestPath))) 
            foreach (var entry in reader.ReadEntries()) {
                var filePath = Path.Combine(manifestDirectory, entry.Path);

                // Audio files do a 1->N mapping so we have to handle that by parsing the format list from the manifest.
                if ((entry.Properties != null) && entry.Properties.ContainsKey("formats")) {
                    foreach (var format in (object[])entry.Properties["formats"])
                        filenames.Add(filePath + (string)format);
                } else {
                    filenames.Add(filePath);
                }
            }

            var fileListPath = archivePath + ".sources";
            using (var fileList = new StreamWriter(fileListPath, false, Encoding.UTF8)) {
                // HACK: 7zip gets really angry about dupes.
                foreach (var filename in filenames.Distinct()) {
                    var relativeFilename = Program.ShortenPath(filename, manifestDirectory);
                    fileList.WriteLine("\"{0}\"", relativeFilename);
                }
            }

            if (!Invoke7Z(
                String.Format(
                    "a -t{0} \"{1}.header\" \"{2}\"",
                    archiveFormat,
                    archivePath,
                    manifestPath
                ),
                workingDirectory: manifestDirectory
            ))
                return false;

            if (Invoke7Z(
                String.Format(
                    "a -t{0} -i@\"{1}\" \"{2}.footer\"",
                    archiveFormat,
                    fileListPath,
                    archivePath
                ),
                workingDirectory: manifestDirectory
            )) {
                File.Delete(fileListPath);

                // HACK: Workaround for 7-zip's utterly dumb behavior of rearranging the contents of .tar files when adding files to them
                using (var output = File.OpenWrite(archivePath)) {
                    var bytes = File.ReadAllBytes(archivePath + ".header");
                    // HACK: Trim the two zero-filled records off the end.
                    output.Write(bytes, 0, bytes.Length - 1024);
                    bytes = File.ReadAllBytes(archivePath + ".footer");
                    output.Write(bytes, 0, bytes.Length);
                }

                File.Delete(archivePath + ".header");
                File.Delete(archivePath + ".footer");

                if (configuration.ArchiveCreator.DeleteArchivedFiles.GetValueOrDefault(true)) {
                    Console.Write("succeeded. Deleting sources ... ");

                    foreach (var filename in filenames)
                        File.Delete(filename);

                    Console.WriteLine("ok");
                } else {
                    Console.WriteLine("succeeded.");
                }
                return true;
            }

            return false;
        }
    }
}
