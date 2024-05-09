using CommandLine;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace UnStandard
{
    internal static class Program
    {
        private static readonly ModuleCreationOptions readOptions = new ModuleCreationOptions
        {
            // Redirect core types to mscorlib 4
            CorLibAssemblyRef = AssemblyRefUser.CreateMscorlibReferenceCLR40()
        };

        private static Options options;

        private static int processed;

        private static int errors;

        private class Options
        {
            [Value(0, MetaName = "input", Required = true, HelpText = "Input files to be processed.")]
            public IEnumerable<string> InputFiles { get; set; }

            [Option('o', "output", Required = true, HelpText = "Output directory.")]
            public string OutputDir { get; set; }

            [Option('v', "verbose", Default = false, HelpText = "Display debugging information about progress.")]
            public bool Verbose { get; set; }

            [Option('n', "dry-run", Default = false, HelpText = "Don't output any files.")]
            public bool DryRun { get; set; }

            [Option('f', "overwrite", Default = false, HelpText = "Overwrite existing files.")]
            public bool Overwrite { get; set; }

            [Option(Default = true, HelpText = "Strip InternalCall from methods and generate a dummy body.")]
            public bool? StripInternal { get; set; }

            [Option(Default = true, HelpText = "Strip TargetFramework attribute from module.")]
            public bool? StripTarget { get; set; }
        }

        public static int Main(string[] args)
        {
            // Parse options
            options = Parser.Default.ParseArguments<Options>(args).WithNotParsed(_ => Environment.Exit(1)).Value;
            // Verify options
            if (!Directory.Exists(options.OutputDir))
            {
                LogError("Output directory does not exist");
                return 1;
            }
            // Process all files
            Stopwatch timer = new Stopwatch();
            timer.Start();
            foreach (string searchPath in options.InputFiles)
            {
                if (File.Exists(searchPath))
                {
                    ProcessFile(searchPath);
                }
                else if (Directory.Exists(searchPath))
                {
                    foreach (string path in Directory.GetFiles(searchPath))
                    {
                        ProcessFile(path);
                    }
                }
                else
                {
                    LogError($"No such file or directory: {searchPath}");
                }
            }
            timer.Stop();
            Console.WriteLine($"Processed {processed} files in {timer.Elapsed}");
            Console.WriteLine($"Encountered {errors} errors");
            return 0;
        }

        private static void ProcessFile(string path)
        {
            // Check if file already exists
            string baseName = Path.GetFileName(path);
            string outputFile = Path.Combine(options.OutputDir, baseName);
            if (File.Exists(outputFile) && !options.Overwrite)
            {
                LogWarning($"Not overwriting {baseName}");
                return;
            }
            // Load file
            ModuleDefMD module;
            try
            {
                module = ModuleDefMD.Load(path, readOptions);
            }
            catch (BadImageFormatException e)
            {
                // Not a .NET dll
                if (options.Verbose)
                {
                    LogWarning($"{e.Message}: {path}", false);
                }
                return;
            }
            if (options.Verbose)
            {
                if (processed > 0)
                {
                    Console.WriteLine();
                }
                Console.WriteLine($"Processing {path}");
            }
            // Check if dll actually uses netstandard
            if (module.GetAssemblyRef("netstandard") == null)
            {
                Console.WriteLine($"{Path.GetFileName(path)} does not use netstandard");
                return;
            }
            // Remap types in module
            int patched = 0;
            foreach (TypeRef typeRef in module.GetTypeRefs())
            {
                if (FixScope(typeRef))
                {
                    patched++;
                }
            }
            foreach (DeclSecurity security in module.Assembly.DeclSecurities)
            {
                foreach (SecurityAttribute attribute in security.SecurityAttributes)
                {
                    if (attribute.AttributeType is TypeRef typeRef && FixScope(typeRef))
                    {
                        patched++;
                    }
                }
            }
            if (options.Verbose)
            {
                Console.WriteLine($"Patched {patched} type references");
            }
            // Strip off the TargetFrameworkAttribute
            if (options.StripTarget == true)
            {
                CustomAttributeCollection asmAttributes = module.Assembly.CustomAttributes;
                for (int i = 0; i < asmAttributes.Count; i++)
                {
                    CustomAttribute attribute = asmAttributes[i];
                    if (attribute.Constructor.FullName == "System.Void System.Runtime.Versioning.TargetFrameworkAttribute::.ctor(System.String)")
                    {
                        asmAttributes.RemoveAt(i);
                        i--;
                    }
                }
            }
            if (options.StripInternal == true)
            {
                // Process all methods in all types
                foreach (TypeDef type in module.Types)
                {
                    ProcessType(type);
                }
            }
            if (!options.DryRun)
            {
                // Preserve all but #Strings and #Blob heaps (netstandard string exists in there)
                ModuleWriterOptions writeOptions = new ModuleWriterOptions(module);
                writeOptions.MetadataOptions.Flags = MetadataFlags.PreserveRids | MetadataFlags.PreserveUSOffsets | MetadataFlags.PreserveExtraSignatureData;
                // Write modified module to output directory
                module.Write(outputFile, writeOptions);
            }
            processed++;
        }

        private static void ProcessType(TypeDef type)
        {
            // Gather a list of candidate set methods
            List<MethodDef> setMethods = new List<MethodDef>();
            foreach (MethodDef method in type.Methods)
            {
                if (method.IsInternalCall && (method.Name.StartsWith("Set") || method.Name.StartsWith("set")) && method.GetParamCount() == 1 && !method.HasReturnType)
                {
                    setMethods.Add(method);
                }
            }
            // Search for getter and setter pairs
            foreach (MethodDef method in type.Methods)
            {
                if (method.IsInternalCall)
                {
                    UTF8String name = method.Name;
                    if (name.StartsWith("Get") || name.StartsWith("get"))
                    {
                        // Check if a corresponding set method exists
                        string targetName;
                        if (name.StartsWith("G"))
                        {
                            targetName = "S" + name.Substring(1);
                        }
                        else
                        {
                            targetName = "s" + name.Substring(1);
                        }
                        MethodDef setMethod = null;
                        if (!method.HasParams() && method.HasReturnType)
                        {
                            foreach (MethodDef candidate in setMethods)
                            {
                                if (candidate.Name == targetName && method.ReturnType.FullName == candidate.GetParam(0).FullName && method.IsStatic == candidate.IsStatic)
                                {
                                    setMethod = candidate;
                                    break;
                                }
                            }
                        }
                        else if (method.GetParamCount() == 1 && !method.HasReturnType && method.GetParam(0).IsByRef)
                        {
                            TypeSig getParam = method.GetParam(0);
                            foreach (MethodDef candidate in setMethods)
                            {
                                if (candidate.Name == targetName && getParam.FullName == candidate.GetParam(0).FullName && method.IsStatic == candidate.IsStatic)
                                {
                                    setMethod = candidate;
                                    break;
                                }
                            }
                        }
                        if (setMethod != null)
                        {
                            // Found get/set pair, generate field name
                            string fieldName = name.Substring(3);
                            if (fieldName.StartsWith("_"))
                            {
                                fieldName = fieldName.Substring(1);
                            }
                            for (int i = 0; ; i++)
                            {
                                string testName = $"<{fieldName}>k__BackingField{(i == 0 ? "" : i.ToString())}";
                                if (type.GetField(testName) == null)
                                {
                                    fieldName = testName;
                                    break;
                                }
                            }
                            // Inject field to back it
                            string typeFullName = type.FullName;
                            if (options.Verbose)
                            {
                                Console.WriteLine($"{typeFullName}: Adding new field: {fieldName}");
                            }
                            TypeSig fieldType = setMethod.GetParam(0);
                            if (fieldType.IsByRef)
                            {
                                fieldType = (fieldType as ByRefSig).Next;
                            }
                            FieldDef newField = new FieldDefUser(fieldName, new FieldSig(fieldType), FieldAttributes.Private | (method.IsStatic ? FieldAttributes.Static : 0));
                            type.Fields.Add(newField);
                            if (options.Verbose)
                            {
                                Console.WriteLine($"{typeFullName}: Patching {method.FullName.Replace(typeFullName, "")} and {setMethod.FullName.Replace(typeFullName, "")}");
                            }
                            if (method.HasReturnType)
                            {
                                // Normal getter/setter
                                // Add body to getter
                                method.IsInternalCall = false;
                                method.Body = new CilBody();
                                if (!method.IsStatic)
                                {
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_0));
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldfld, newField));
                                }
                                else
                                {
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldsfld, newField));
                                }
                                method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                                // Add body to setter
                                setMethod.IsInternalCall = false;
                                setMethod.Body = new CilBody();
                                setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_0));
                                if (!setMethod.IsStatic)
                                {
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_1));
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Stfld, newField));
                                }
                                else
                                {
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Stsfld, newField));
                                }
                                setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                            }
                            else
                            {
                                // ByRef struct parameter
                                ITypeDefOrRef objType;
                                if (fieldType.IsTypeDefOrRef)
                                {
                                    objType = fieldType.ToTypeDefOrRef();
                                }
                                else
                                {
                                    LogError($"Unhandled field TypeSig stripping {fieldType.GetType()}");
                                    continue;
                                }
                                // Add body to getter
                                method.IsInternalCall = false;
                                method.Body = new CilBody();
                                if (!method.IsStatic)
                                {
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_1));
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_0));
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldfld, newField));
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Stobj, objType));
                                }
                                else
                                {
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_0));
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Ldsfld, newField));
                                    method.Body.Instructions.Add(new Instruction(OpCodes.Stobj, objType));
                                }
                                method.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                                // Add body to setter
                                setMethod.IsInternalCall = false;
                                setMethod.Body = new CilBody();
                                if (!setMethod.IsStatic)
                                {
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_0));
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_1));
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ldobj, objType));
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Stfld, newField));
                                }
                                else
                                {
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ldarg_0));
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ldobj, objType));
                                    setMethod.Body.Instructions.Add(new Instruction(OpCodes.Stsfld, newField));
                                }
                                setMethod.Body.Instructions.Add(new Instruction(OpCodes.Ret));
                            }
                        }
                    }
                }
            }
            // Strip InternalCall from all remaining methods
            foreach (MethodDef method in type.Methods)
            {
                if (method.IsInternalCall)
                {
                    if (options.Verbose)
                    {
                        Console.WriteLine($"Stripping InternalCall from {method.FullName}");
                    }
                    method.IsInternalCall = false;
                    method.Body = new CilBody();
                    // TODO: Initialize out parameters
                    // Handle return value
                    IList<Instruction> instructions = method.Body.Instructions;
                    ElementType returnElement = method.ReturnType.RemovePinnedAndModifiers().GetElementType();
                    if (returnElement != ElementType.Void)
                    {
                        TypeSig returnType = method.ReturnType;
                        if (returnElement == ElementType.ValueType)
                        {
                            if (returnType.IsTypeDefOrRef)
                            {
                                ITypeDefOrRef objType = returnType.ToTypeDefOrRef();
                                Local local = method.Body.Variables.Add(new Local(returnType));
                                instructions.Add(new Instruction(OpCodes.Ldloca_S, local));
                                instructions.Add(new Instruction(OpCodes.Initobj, objType));
                                instructions.Add(new Instruction(OpCodes.Ldloc_0));
                            }
                            else
                            {
                                LogError($"Unhandled return TypeSig stripping {returnType.GetType()}");
                            }
                        }
                        else if (returnType.IsValueType)
                        {
                            if (returnElement == ElementType.R4)
                            {
                                instructions.Add(new Instruction(OpCodes.Ldc_R4, 0f));
                            }
                            else if (returnElement == ElementType.R8)
                            {
                                instructions.Add(new Instruction(OpCodes.Ldc_R8, 0d));
                            }
                            else
                            {
                                instructions.Add(new Instruction(OpCodes.Ldc_I4_0));
                                if (returnElement == ElementType.I8 || returnElement == ElementType.U8)
                                {
                                    instructions.Add(new Instruction(OpCodes.Conv_I8));
                                }
                                else if (returnElement == ElementType.I)
                                {
                                    instructions.Add(new Instruction(OpCodes.Conv_I));
                                }
                                else if (returnElement == ElementType.U)
                                {
                                    instructions.Add(new Instruction(OpCodes.Conv_U));
                                }
                            }
                        }
                        else
                        {
                            instructions.Add(new Instruction(OpCodes.Ldnull));
                        }
                    }
                    instructions.Add(new Instruction(OpCodes.Ret));
                }
            }
            foreach (TypeDef nested in type.NestedTypes)
            {
                ProcessType(nested);
            }
        }

        private static bool FixScope(TypeRef type)
        {
            if (type.ResolutionScope.Name == "netstandard")
            {
                // Strip off nested type
                string fullName = type.FullName;
                if (fullName.Contains("/"))
                {
                    fullName = fullName.Split('/')[0];
                }
                // Check if we can remap this type
                if (StandardTypes.frameworkTypes.TryGetValue(fullName, out string assembly))
                {
                    type.ResolutionScope = StandardTypes.assemblyInfo[assembly];
                    return true;
                }
                else
                {
                    LogError($"No mapping entry exists for type {fullName}");
                }
            }
            return false;
        }

        public static void LogWarning(object message, bool error = true)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: {message}");
            Console.ResetColor();
            if (error)
            {
                errors++;
            }
        }

        public static void LogError(object message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {message}");
            Console.ResetColor();
            errors++;
        }
    }
}
