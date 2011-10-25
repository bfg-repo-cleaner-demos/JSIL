﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mono.Cecil;

namespace JSIL {
    public class AssemblyManifest {
        protected readonly Dictionary<string, Int32> AssemblyIDs = new Dictionary<string, int>();

        public string GetPrivateToken (AssemblyDefinition assembly) {
            return GetPrivateToken(assembly.FullName);
        }

        public string GetPrivateToken (string assemblyFullName) {
            Int32 result;

            if (!AssemblyIDs.TryGetValue(assemblyFullName, out result)) {
                result = AssemblyIDs.Count;
                AssemblyIDs[assemblyFullName] = result;
            }

            return String.Format("$asm{0:X2}", result);
        }
    }
}
