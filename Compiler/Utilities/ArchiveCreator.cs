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

        public static void CreateArchiveFromManifest (VariableSet variables, Configuration configuration, string manifestPath) {
            var manifestDirectory = Path.GetDirectoryName(manifestPath);
            var archiveFormat = configuration.ArchiveCreator.Format;
            var archiveName = String.Format("{0}.{1}", Path.GetFileName(manifestPath).Replace(".manifest.js", ""), archiveFormat);
            var archivePath = Path.Combine(manifestDirectory, archiveName);

            Console.Write("// Creating '{0}'... ", Path.GetFileName(archivePath));
            if (File.Exists(archivePath))
                File.Delete(archivePath);

            var fileListPath = archivePath + ".sources";

            using (var reader = new ContentManifestReader(File.OpenText(manifestPath)))
            using (var fileList = new StreamWriter(fileListPath, false, Encoding.UTF8)) {
                fileList.WriteLine("\"{0}\"", manifestPath);

                foreach (var entry in reader.ReadEntries()) {
                    var filePath = Path.Combine(manifestDirectory, entry.Path);
                    fileList.WriteLine("\"{0}\"", filePath);
                }
            }

            string stderr;
            byte[] stdout;

            var exitCode = IOUtil.Run(
                SevenZipPath,
                String.Format(
                    "a -t{0} -i@\"{1}\" \"{2}\"",
                    archiveFormat,
                    fileListPath,
                    archivePath
                ),
                null, out stderr, out stdout
            );

            if (exitCode == 0) {
                File.Delete(fileListPath);
                Console.WriteLine("succeeded.");
            } else {
                Console.WriteLine("failed.");
                Console.WriteLine(Encoding.UTF8.GetString(stdout));
                Console.WriteLine(stderr);
            }
        }
    }
}
