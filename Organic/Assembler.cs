using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Reflection;
using System.Globalization;
using Organic.Plugins;

namespace Organic
{
    /// <summary>
    ///  Organic Assembler Program
    /// </summary>
    public partial class Assembler
    {
        #region Runtime values

        public ushort currentAddress;
        public Stack<string> FileNames;
        // Suspended line counts are the number of lines that should be considered the same line, for instance, upon expanding a macro
        public Stack<int> LineNumbers, SuspendedLineCounts;
        public Stack<string> WorkingDirectories; 
        public Dictionary<string, byte> OpcodeTable;
        public Dictionary<string, byte> NonBasicOpcodeTable;
        public Dictionary<string, byte> ValueTable;
        public Dictionary<int, ushort> RelativeLabels; // line, value
        public List<string> ReferencedValues;
        public string PriorGlobalLabel = "";
        public Stack<bool> IfStack;
        public bool noList;
        public bool ForceLongLiterals = false;
        private List<ushort> RelocatedAddresses { get; set; }
        private int TableInsertionIndex { get; set; }
        private int RootLineNumber { get; set; }
        private ushort OldAddress { get; set; }
        private int RelocationGroup { get; set; }
        public bool IsRelocating { get; set; }
        private int UniqueScopeNumber { get; set; }

        private List<string> IncludedFiles;

        /// <summary>
        /// Values (such as labels and equates) found in the code
        /// </summary>
        public Dictionary<string, ushort> Values;
        //public Dictionary<string, ushort> LabelValues;
        public List<Label> LabelValues;
        public List<Macro> Macros;

        /// <summary>
        /// Path to search for include files in.
        /// </summary>
        public string IncludePath;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes all values for this assembler.  Assembler is designed to handle
        /// one assembly per instance.  If you intend to assemble several times, create
        /// new instances of this class each time.
        /// </summary>
        public Assembler()
        {
            // All default values for the assembler
            currentAddress = 0;

            // Load table
            OpcodeTable = new Dictionary<string, byte>();
            NonBasicOpcodeTable = new Dictionary<string, byte>();
            ValueTable = new Dictionary<string, byte>();
            IfStack = new Stack<bool>();
            WorkingDirectories = new Stack<string>();
            noList = false;

            LoadTable();

            Values = new Dictionary<string, ushort>();
            LabelValues = new List<Label>();
            Macros = new List<Macro>();

            RelativeLabels = new Dictionary<int, ushort>();

            LineNumbers = new Stack<int>();
            SuspendedLineCounts = new Stack<int>();
            FileNames = new Stack<string>();
            IncludedFiles = new List<string>();

            ExpressionExtensions = new Dictionary<string, ExpressionExtension>();
            ReferencedValues = new List<string>();
            LoadInternalExpressionExtensions();

            TableInsertionIndex = -1;
            RelocationGroup = -1;

            UniqueScopeNumber = 0;

            LoadPlugins();
        }

        private void LoadTable()
        {
            StreamReader sr = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream("Organic.DCPUtable.txt"));
            string[] lines = sr.ReadToEnd().Replace("\r", "").Split('\n');
            sr.Close();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#"))
                    continue;
                string[] parts = line.Split(' ');
                if (parts[0] == "o")
                    OpcodeTable.Add(parts[2], byte.Parse(parts[1], NumberStyles.HexNumber));
                else if (parts[0] == "n")
                    NonBasicOpcodeTable.Add(parts[2], byte.Parse(parts[1], NumberStyles.HexNumber));
                else if (parts[0] == "a,b")
                    ValueTable.Add(parts[2], byte.Parse(parts[1], NumberStyles.HexNumber));
            }
        }

        #endregion

        #region Assembler

        /// <summary>
        /// Assembles the provided code.
        /// This will use the current directory to fetch include files and such.
        /// </summary>
        /// <param name="code"></param>
        /// <returns></returns>
        public List<ListEntry> Assemble(string code)
        {
            return Assemble(code, "sourceFile");
        }

        /// <summary>
        /// Assembles the provided code.
        /// This will use the current directory to fetch include files and such.
        /// </summary>
        /// <returns>A listing for the code</returns>
        public List<ListEntry> Assemble(string code, string FileName)
        {
            FileNames = new Stack<string>();
            LineNumbers = new Stack<int>();
            FileNames.Push(FileName);
            LineNumbers.Push(0);
            RootLineNumber = 0;
            IfStack.Push(true);

            // Pass one
            string[] lines = code.Replace("\r", "").Split('\n');
            List<ListEntry> output = new List<ListEntry>();
            for (int i = 0; i < lines.Length; i++)
            {
                if (SuspendedLineCounts.Count == 0)
                {
                    LineNumbers.Push(LineNumbers.Pop() + 1);
                    RootLineNumber++;
                }
                else
                {
                    int count = SuspendedLineCounts.Pop();
                    count--;
                    if (count > 0)
                        SuspendedLineCounts.Push(count);
                }

                string line = lines[i].TrimComments().TrimExcessWhitespace();
                if (string.IsNullOrEmpty(line))
                    continue;
                string[] sublines = line.SafeSplit('\\');
                if (sublines.Length > 1)
                {
                    string[] newLines = new string[lines.Length + sublines.Length - 1];
                    Array.Copy(lines, 0, newLines, 0, i);
                    Array.Copy(sublines, 0, newLines, i, sublines.Length);
                    if (lines.Length > i + 1)
                        Array.Copy(lines, i + 1, newLines, i + sublines.Length, lines.Length - i - 1);
                    lines = newLines;
                    i--;
                    SuspendedLineCounts.Push(sublines.Length);
                    continue;
                }
                ListEntry listEntry = new ListEntry(line, FileNames.Peek(), LineNumbers.Peek(), currentAddress, !noList);
                listEntry.RootLineNumber = RootLineNumber;
                if (HandleCodeLine != null)
                {
                    HandleCodeEventArgs args = new HandleCodeEventArgs();
                    args.Code = line;
                    args.Handled = false;
                    args.Output = listEntry;
                    HandleCodeLine(this, args);
                    if (args.Handled)
                    {
                        output.Add(args.Output);
                        continue;
                    }
                    listEntry = args.Output;
                    line = args.Code;
                }
                if (line.SafeContains(':') && !noList)
                {
                    if (!IfStack.Peek())
                        continue;
                    listEntry.CodeType = CodeType.Directive;
                    // Parse labels
                    string label = line;
                    if (line.StartsWith(":"))
                    {
                        label = label.Substring(1);
                        if (line.Contains(' '))
                            line = line.Substring(line.IndexOf(' ') + 1).Trim();
                        else
                            line = "";
                    }
                    else
                    {
                        label = label.Remove(label.IndexOf(':'));
                        line = line.Substring(line.IndexOf(':') + 1);
                    }
                    line = line.Trim();
                    if (label.Contains(" "))
                        label = label.Remove(label.IndexOf(' '));
                    if (label == "$")
                    {
                        RelativeLabels.Add(GetRootNumber(LineNumbers), currentAddress);
                        output.Add(listEntry);
                        continue;
                    }
                    if (label.Contains(' ') || label.Contains('\t') || !(char.IsLetter(label[0]) || label[0] == '.' || label[0] == '_'))
                    {
                        listEntry.ErrorCode = ErrorCode.InvalidLabel;
                        output.Add(listEntry);
                        continue;
                    }
                    bool invalid = false;
                    if (label.StartsWith("_"))
                    {
                        listEntry.ErrorCode = ErrorCode.InvalidLabel;
                        output.Add(listEntry);
                        continue;
                    }
                    foreach (char c in label)
                    {
                        if (!char.IsLetterOrDigit(c) && c != '_' && c != '.')
                        {
                            listEntry.ErrorCode = ErrorCode.InvalidLabel;
                            output.Add(listEntry);
                            invalid = true;
                            break;
                        }
                    }
                    if (invalid)
                        continue;
                    if (Values.ContainsKey(label) || LabelValues.ContainsKey(label))
                    {
                        listEntry.ErrorCode = ErrorCode.DuplicateName;
                        output.Add(listEntry);
                        continue;
                    }
                    if (label.StartsWith("."))
                        label = PriorGlobalLabel + "_" + label.Substring(1);
                    else
                        PriorGlobalLabel = label;
                    LabelValues.Add(new Label() 
                    {
                        LineNumber = LineNumbers.Peek(),
                        Name = label,
                        RootLineNumber = listEntry.RootLineNumber,
                        Address = currentAddress,
                    });
                    if (!IsRelocating)
                        LabelValues[LabelValues.Count - 1].RelocationGroup = -1;
                    else
                        LabelValues[LabelValues.Count - 1].RelocationGroup = RelocationGroup;
                    listEntry.CodeType = CodeType.Label;
                    output.Add(listEntry);
                }
                if (string.IsNullOrEmpty(line))
                    continue;
                if (line.Contains(".equ") && !line.StartsWith(".equ")) // TASM compatibility
                {
                    line = ".equ " + line.Replace(".equ", "").TrimExcessWhitespace();
                }
                if (line.StartsWith("dat "))
                {
                    line = "." + line;
                }
                if (line.StartsWith(".") || line.StartsWith("#"))
                {
                    // #include has to be handled in this method
                    if (line.StartsWith("#include") || line.StartsWith(".include"))
                    {
                        if (!IfStack.Peek())
                            continue;
                        string includedFileName = line.Substring(line.IndexOf(" ") + 1);
                        includedFileName = includedFileName.Trim('"', '\'');
                        if (includedFileName.StartsWith("<") && includedFileName.EndsWith(">"))
                        {
                            // Find included file
                            includedFileName = includedFileName.Trim('<', '>');
                            string[] paths = IncludePath.Split(new []{';'}, StringSplitOptions.RemoveEmptyEntries );
                            foreach (var path in paths)
                            {
                                if (File.Exists(Path.Combine(path, includedFileName)))
                                {
                                    includedFileName = Path.Combine(path, includedFileName);
                                    break;
                                }
                            }
                        }
                        if (!File.Exists(includedFileName))
                        {
                            listEntry.ErrorCode = ErrorCode.FileNotFound;
                            output.Add(listEntry);
                        }
                        else if (IncludedFiles.Contains(includedFileName))
                        {
                        	
                        }
                        else
                        {
                            using (Stream includedFile = File.Open(includedFileName, FileMode.Open))
                            {
                                StreamReader sr = new StreamReader(includedFile);
                                string contents = sr.ReadToEnd();
                                sr.Close();

                                string[] newSource = contents.Replace("\r", "").Split('\n');
                                string[] newLines = new string[newSource.Length + lines.Length];
                                Array.Copy(lines, newLines, i);
                                Array.Copy(newSource, 0, newLines, i, newSource.Length);
                                newLines[i + newSource.Length] = "#endfile";
                                if (lines.Length > i + 1)
                                    Array.Copy(lines, i + 1, newLines, i + newSource.Length + 1, lines.Length - i - 1);
                                lines = newLines;
                            }
                            WorkingDirectories.Push(Directory.GetCurrentDirectory());
                            if (Path.IsPathRooted(includedFileName))
                                Directory.SetCurrentDirectory(GetDirectory(includedFileName));
                            else
                                Directory.SetCurrentDirectory(Path.Combine(Directory.GetCurrentDirectory(),
                                    GetDirectory(includedFileName)));
                            FileNames.Push(includedFileName);
                            LineNumbers.Push(0);
                            IncludedFiles.Add(includedFileName);
                            i--;
                            continue;
                        }
                    }
                    else if ((line.StartsWith("#incbin") || line.StartsWith(".incbin")) && !noList)
                    {
                        if (!IfStack.Peek())
                            continue;
                        string includedFileName = line.Substring(line.IndexOf(" ") + 1);
                        includedFileName = includedFileName.Trim('"', '\'');
                        if (includedFileName.StartsWith("<") && includedFileName.EndsWith(">"))
                        {
                            // Find included file
                            includedFileName = includedFileName.Trim('<', '>');
                            string[] paths = IncludePath.Split(';');
                            foreach (var path in paths)
                            {
                                if (File.Exists(Path.Combine(path, includedFileName)))
                                {
                                    includedFileName = Path.Combine(path, includedFileName);
                                    break;
                                }
                            }
                        }
                        if (!File.Exists(includedFileName))
                        {
                            listEntry.ErrorCode = ErrorCode.FileNotFound;
                            output.Add(listEntry);
                        }
                        else
                        {
                            using (Stream includedFile = File.Open(includedFileName, FileMode.Open))
                            {
                                byte[] rawData = new byte[includedFile.Length];
                                includedFile.Read(rawData, 0, (int)includedFile.Length);

                                List<ushort> binOutput = new List<ushort>();
                                foreach (byte b in rawData)
                                    binOutput.Add(b);
                                listEntry.Output = binOutput.ToArray();
                                output.Add(listEntry);
                                output[output.Count - 1].CodeType = CodeType.Directive;
                                if (!noList)
                                    currentAddress += (ushort)binOutput.Count;
                            }
                        }
                    }
                    else if ((line.StartsWith("#incpack") || line.StartsWith(".incpack")) && !noList)
                    {
                        if (!IfStack.Peek())
                            continue;
                        string includedFileName = line.Substring(line.IndexOf(" ") + 1);
                        includedFileName = includedFileName.Trim('"', '\'');
                        if (includedFileName.StartsWith("<") && includedFileName.EndsWith(">"))
                        {
                            // Find included file
                            includedFileName = includedFileName.Trim('<', '>');
                            string[] paths = IncludePath.Split(';');
                            foreach (var path in paths)
                            {
                                if (File.Exists(Path.Combine(path, includedFileName)))
                                {
                                    includedFileName = Path.Combine(path, includedFileName);
                                    break;
                                }
                            }
                        }
                        if (!File.Exists(includedFileName))
                        {
                            listEntry.ErrorCode = ErrorCode.FileNotFound;
                            output.Add(listEntry);
                        }
                        else
                        {
                            using (Stream includedFile = File.Open(includedFileName, FileMode.Open))
                            {
                                byte[] rawData = new byte[includedFile.Length];
                                includedFile.Read(rawData, 0, (int)includedFile.Length);

                                List<ushort> binOutput = new List<ushort>();
                                for (int j = 0; j < rawData.Length; j += 2)
                                {
                                    binOutput.Add((ushort)(
                                        rawData[j + 1] |
                                        (rawData[j] << 8)
                                        ));
                                }
                                listEntry.Output = binOutput.ToArray();
                                output.Add(listEntry);
                                output[output.Count - 1].CodeType = CodeType.Directive;
                                if (!noList)
                                    currentAddress += (ushort)binOutput.Count;
                            }
                        }
                    }
                    else if (line == "#endfile" || line == ".endfile")
                    {
                        if (!IfStack.Peek())
                            continue;
                        FileNames.Pop();
                        LineNumbers.Pop();
                        RootLineNumber--;
                        Directory.SetCurrentDirectory(WorkingDirectories.Pop());
                    }
                    else if (line.StartsWith(".macro") && !noList)
                    {
                        if (!IfStack.Peek())
                            continue;
                        string macroDefinition = line.Substring(7).Trim();
                        Macro macro = new Macro();
                        macro.Args = new string[0];
                        if (macroDefinition.EndsWith("{"))
                            macroDefinition = macroDefinition.Remove(macroDefinition.Length - 1).Trim();
                        if (macroDefinition.Contains("("))
                        {
                            string paramDefinition = macroDefinition.Substring(macroDefinition.IndexOf("(") + 1);
                            macro.Name = macroDefinition.Remove(macroDefinition.IndexOf("(")).Trim();
                            if (!paramDefinition.EndsWith(")"))
                            {
                                listEntry.ErrorCode = ErrorCode.InvalidMacroDefintion;
                                output.Add(listEntry);
                            }
                            else
                            {
                                paramDefinition = paramDefinition.Remove(paramDefinition.Length - 1);
                                if (paramDefinition.Length > 0)
                                {
                                    string[] parameters = paramDefinition.Split(',');
                                    bool continueEvaluation = true;
                                    for (int j = 0; j < parameters.Length; j++)
                                    {
                                        string parameter = parameters[j].Trim();
                                        if (!char.IsLetter(parameter[0]))
                                        {
                                            continueEvaluation = false;
                                            break;
                                        }
                                        foreach (char c in parameter)
                                        {
                                            if (!char.IsLetterOrDigit(c) && c != '_')
                                            {
                                                continueEvaluation = false;
                                                break;
                                            }
                                        }
                                        if (!continueEvaluation)
                                            break;
                                        macro.Args = macro.Args.Concat(new string[] { parameter }).ToArray();
                                    }
                                    if (!continueEvaluation)
                                        continue;
                                }
                            }
                        }
                        else
                            macro.Name = macroDefinition;
                        // Isolate macro code
                        macro.Code = "";
                        bool foundEndmacro = false;
                        string macroLine = line;
                        i++;
                        for (; i < lines.Length; i++)
                        {
                            line = lines[i].TrimComments().TrimExcessWhitespace();
                            LineNumbers.Push(LineNumbers.Pop() + 1);
                            if (line == ".endmacro" || line == "#endmacro" || line == "}")
                            {
                                foundEndmacro = true;
                                break;
                            }
                            if (line != "{")
                                macro.Code += "\n" + line;
                        }
                        if (!foundEndmacro)
                        {
                            listEntry.ErrorCode = ErrorCode.UncoupledStatement;
                            output.Add(listEntry);
                            continue;
                        }
                        macro.Code = macro.Code.Trim('\n');
                        Macros.Add(macro);
                        output.Add(new ListEntry(".macro " + macroDefinition, FileNames.Peek(), LineNumbers.Peek(), currentAddress));
                        output[output.Count - 1].CodeType = CodeType.Directive;
                        foreach (var codeLine in macro.Code.Split('\n'))
                        {
                            output.Add(new ListEntry(codeLine, FileNames.Peek(), LineNumbers.Peek(), currentAddress));
                            output[output.Count - 1].CodeType = CodeType.Directive;
                        }
                        output.Add(new ListEntry(".endmacro", FileNames.Peek(), LineNumbers.Peek(), currentAddress));
                        output[output.Count - 1].CodeType = CodeType.Directive;
                    }
                    else
                    {
                        // Parse preprocessor directives
                        ParseDirectives(output, line);
                    }
                }
                else
                {
                    if (!IfStack.Peek())
                        continue;
                    // Search through macros
                    bool mayHaveMacro = false;
                    foreach (Macro macro in Macros)
                    {
                        if (line.StartsWith(macro.Name))
                        {
                            mayHaveMacro = true;
                            break;
                        }
                    }
                    if (line.SafeContains('(') && line.SafeContains(')') && mayHaveMacro)
                    {
                        Macro userMacro = new Macro();
                        userMacro.Args = new string[0];
                        string macroDefinition = line;
                        string paramDefinition = macroDefinition.Substring(macroDefinition.IndexOf("(") + 1);
                        userMacro.Name = macroDefinition.Remove(macroDefinition.IndexOf("(")).Trim();
                        if (!paramDefinition.EndsWith(")"))
                        {
                            listEntry.ErrorCode = ErrorCode.InvalidMacroDefintion;
                            output.Add(listEntry);
                        }
                        else
                        {
                            paramDefinition = paramDefinition.Remove(paramDefinition.Length - 1);
                            if (paramDefinition.Length > 0)
                            {
                                string[] parameters = paramDefinition.SafeSplit(',');
                                for (int j = 0; j < parameters.Length; j++)
                                {
                                    string parameter = parameters[j].Trim();
                                    userMacro.Args = userMacro.Args.Concat(new[] { parameter }).ToArray();
                                }
                            }
                        }
                        bool macroMatched = false;
                        foreach (Macro macro in Macros)
                        {
                            if (macro.Name == userMacro.Name &&
                                macro.Args.Length == userMacro.Args.Length)
                            {
                                // Expand the macro
                                userMacro.Code = macro.Code;
                                for (int j = 0; j < macro.Args.Length; j++)
                                    userMacro.Code = userMacro.Code.Replace(macro.Args[j], userMacro.Args[j]);
                                string[] macroCode = userMacro.Code.Replace("\r", "\n").Split('\n');
                                string[] newLines = new string[lines.Length + macroCode.Length - 1];
                                Array.Copy(lines, 0, newLines, 0, i);
                                Array.Copy(macroCode, 0, newLines, i, macroCode.Length);
                                if (lines.Length > i + 1)
                                    Array.Copy(lines, i + 1, newLines, i + macroCode.Length, lines.Length - i - 1);
                                lines = newLines;
                                output.Add(listEntry);
                                output[output.Count - 1].CodeType = CodeType.Directive;
                                line = lines[i].TrimComments().TrimExcessWhitespace();
                                macroMatched = true;
                                SuspendedLineCounts.Push(macroCode.Length); // Suspend the line counts for the expanded macro
                            }
                        }
                        if (macroMatched)
                        {
                            i--;
                            continue;
                        }
                        // We'll just let the opcode matcher yell at them if it isn't found
                    }

                    // Check for OPCodes
                    var opcode = MatchString(line, OpcodeTable);
                    bool nonBasic = false;
                    if (opcode == null)
                    {
                        opcode = MatchString(line, NonBasicOpcodeTable);
                        nonBasic = true;
                    }
                    if (opcode == null)
                    {
                        listEntry.ErrorCode = ErrorCode.InvalidOpcode;
                        output.Add(listEntry);
                        continue;
                    }
                    else
                    {
                        listEntry.Opcode = opcode;
                        StringMatch valueA = null, valueB = null;
                        listEntry.Output = new ushort[1];
                        if (!nonBasic)
                        {
                            listEntry.CodeType = CodeType.BasicInstruction;
                            if (opcode.valueA != null)
                                valueA = MatchString(opcode.valueA, ValueTable);
                            if (opcode.valueB != null)
                                valueB = MatchString(opcode.valueB, ValueTable);
                            if (nonBasic == false && (opcode.valueA == null || opcode.valueB == null))
                            {
                                listEntry.ErrorCode = ErrorCode.InvalidOpcode;
                                output.Add(listEntry);
                                continue;
                            }
                            if (valueA.value == valueB.value && valueA.value != 0x1E && valueB.value != 0x1E && opcode.value == 0x1)
                                listEntry.WarningCode = WarningCode.RedundantStatement;
                            if (valueB.value == 0x1F && !opcode.match.Contains("IF"))
                                listEntry.WarningCode = WarningCode.AssignToLiteral;
                            listEntry.ValueA = valueA;
                            listEntry.ValueB = valueB;
                            // De-localize labels
                            if (listEntry.ValueA.isLiteral)
                            {
                                listEntry.Output = listEntry.Output.Concat(new ushort[1]).ToArray();
                                var result = ParseExpression(listEntry.ValueA.literal);
                                foreach (var reference in result.References)
                                {
                                    if (reference.StartsWith("."))
                                        listEntry.ValueA.literal = listEntry.ValueA.literal.Replace(reference,
                                            PriorGlobalLabel + "_" + reference.Substring(1));
                                }
                            }
                            if (listEntry.ValueB.isLiteral)
                            {
                                listEntry.Output = listEntry.Output.Concat(new ushort[1]).ToArray();
                                var result = ParseExpression(listEntry.ValueB.literal);
                                foreach (var reference in result.References)
                                {
                                    if (reference.StartsWith("."))
                                        listEntry.ValueB.literal = listEntry.ValueB.literal.Replace(reference,
                                            PriorGlobalLabel + "_" + reference.Substring(1));
                                }
                            }
                        }
                        else
                        {
                            listEntry.CodeType = CodeType.NonBasicInstruction;
                            if (opcode.valueA != null)
                                valueA = MatchString(opcode.valueA, ValueTable);
                            listEntry.ValueA = valueA;
                            // De-localize labels
                            if (listEntry.ValueA.isLiteral)
                            {
                                listEntry.Output = listEntry.Output.Concat(new ushort[1]).ToArray();
                                var result = ParseExpression(listEntry.ValueA.literal);
                                foreach (var reference in result.References)
                                {
                                    if (reference.StartsWith(".") || reference.StartsWith("_"))
                                        listEntry.ValueA.literal = listEntry.ValueA.literal.Replace(reference,
                                            PriorGlobalLabel + "_" + reference.Substring(1));
                                }
                            }
                        }
                        output.Add(listEntry);
                        currentAddress++;
                        if (valueA != null)
                            if (valueA.isLiteral)
                                currentAddress++;
                        if (valueB != null)
                            if (valueB.isLiteral)
                                currentAddress++;
                    }
                }
            }
            return EvaluateAssembly(output);
        }

        private string GetDirectory(string filePath)
        {
            int forward = filePath.LastIndexOf('/');
            int backward = filePath.LastIndexOf('\\');
            if (forward == -1 && backward == -1)
                return ".";
            if (forward < backward)
                return filePath.Remove(backward);
            return filePath.Remove(forward);
        }

        private List<ListEntry> EvaluateAssembly(List<ListEntry> output)
        {
            bool finishedAssembly = false, inMacro = false;
            int iterations = 0;
            List<string> RelocatedLabels = new List<string>();
            RelocationGroup = -1;
            while (!finishedAssembly)
            {
                finishedAssembly = true;
                iterations++;
                if (iterations > 10000)
                {
                    Console.WriteLine("ERROR: Organic has surpassed 10,000 passes of the file, and suspects circular references.");
                    Console.WriteLine("Assembly will now terminate, and your output files may be inaccurate.");
                    return output;
                }
                LineNumbers = new Stack<int>();
                LineNumbers.Push(0);
                UniqueScopeNumber = 0;
                for (int i = 0; i < output.Count; i++)
                {
                    LineNumbers.Pop();
                    LineNumbers.Push(output[i].LineNumber);
                    foreach (var kvp in output[i].PostponedExpressions)
                    {
                        ExpressionResult result = ParseExpression(kvp.Value);
                        if (!result.Successful)
                        {
                            output[i].ErrorCode = ErrorCode.IllegalExpression;
                            continue;
                        }
                        output[i].Output[kvp.Key] = result.Value;
                    }
                    if (output[i].CodeType == CodeType.Label && !output[i].Code.StartsWith("."))
                    {
                        if (output[i].Code.StartsWith(":"))
                            output[i].Code = output[i].Code.Substring(1) + ":";
                        if (!output[i].Code.StartsWith("."))
                            PriorGlobalLabel = output[i].Code.Remove(output[i].Code.Length - 1);
                    }
                    if (output[i].Code == ".longform" || output[i].Code == "#longform")
                    {
                        ForceLongLiterals = true;
                        continue;
                    }
                    if (output[i].Code == ".shortform" || output[i].Code == "#shortform")
                    {
                        ForceLongLiterals = false;
                        continue;
                    }
                    if (output[i].Code == ".relocate" || output[i].Code == "#relocate")
                    {
                        RelocatedAddresses = new List<ushort>();
                        TableInsertionIndex = i;
                        RelocationGroup++;
                    }
                    if (output[i].Code == ".endrelocate" || output[i].Code == "#endrelocate")
                    {
                        output[TableInsertionIndex].Output = new ushort[] { (ushort)RelocatedAddresses.Count }.Concat(RelocatedAddresses).ToArray();
                        TableInsertionIndex = -1;
                    }
                    if (output[i].Code == ".uniquescope" && !inMacro)
                        PriorGlobalLabel = "_unique" + UniqueScopeNumber++;
                    if (output[i].Code.StartsWith(".macro ") || output[i].Code.StartsWith("#macro "))
                        inMacro = true;
                    if (output[i].Code == ".endmacro" || output[i].Code == "#endmacro")
                        inMacro = false;
                    if (output[i].Opcode != null && output[i].ErrorCode == ErrorCode.Success)
                    {
                        // Assemble output
                        if (output[i].CodeType == CodeType.BasicInstruction)
                        {
                            byte value = output[i].Opcode.value;
                            byte valueA = output[i].ValueA.value;
                            byte valueB = output[i].ValueB.value;
                            ushort? valueBResult = null;
                            ushort? valueAResult = null;
                            bool relocateA = false, relocateB = false;
                            if (output[i].ValueB.isLiteral)
                            {
                                ExpressionResult result = ParseExpression(output[i].ValueB.literal);
                                if (TableInsertionIndex != -1)
                                {
                                    foreach (var label in LabelValues.Where(l => l.RelocationGroup == RelocationGroup))
                                    {
                                        foreach (var needle in result.References)
                                            if (needle == label.Name)
                                            {
                                                relocateB = true;
                                                break;
                                            }
                                    }
                                }
                                if (!result.Successful)
                                {
                                    output[i].ErrorCode = ErrorCode.IllegalExpression;
                                    continue;
                                }
                                valueBResult = result.Value;
                            }
                            if (output[i].ValueA.isLiteral) // next-word
                            {
                                ExpressionResult result = ParseExpression(output[i].ValueA.literal);
                                if (TableInsertionIndex != -1)
                                {
                                    foreach (var label in LabelValues.Where(l => l.RelocationGroup == RelocationGroup))
                                    {
                                        foreach (var needle in result.References)
                                            if (needle == label.Name)
                                            {
                                                relocateA = true;
                                                break;
                                            }
                                    }
                                }
                                if (!result.Successful)
                                {
                                    output[i].ErrorCode = ErrorCode.IllegalExpression;
                                    continue;
                                }
                                if ((result.Value == 0xFFFF || result.Value <= 30) && !ForceLongLiterals && TableInsertionIndex == -1 && output[i].ValueA.value == 0x1F)
                                {
                                    // Short form literal
                                    valueA = (byte)(result.Value + 0x21);
                                    if (output[i].Output.Length - (output[i].ValueB.isLiteral ? 1 : 0) != 1)
                                    {
                                        finishedAssembly = false;
                                        output[i].Output = output[i].Output.Take(1).ToArray();
                                        output[i].Output[0] = (ushort)(value | (valueB << 5) | (valueA << 10));
                                        if (valueAResult.HasValue)
                                            output[i].Output = output[i].Output.Concat(new ushort[] { valueAResult.Value }).ToArray();
                                        if (valueBResult.HasValue)
                                            output[i].Output = output[i].Output.Concat(new ushort[] { valueBResult.Value }).ToArray();
                                        // if the size of the instruction has changed
                                        int lineNumber = output[i].RootLineNumber;
                                        int maxLineNumber = int.MaxValue;
                                        for (; i < output.Count; i++)
                                        {
                                            if (output[i].Code.StartsWith(".org") || output[i].Code.StartsWith("#org"))
                                            {
                                                maxLineNumber = output[i].RootLineNumber;
                                                break;
                                            }
                                            if (output[i].RootLineNumber > lineNumber)
                                                output[i].Address--;
                                        }
                                        foreach (Label l in LabelValues)
                                        {
                                            if (l.RootLineNumber >= lineNumber && l.RootLineNumber <= maxLineNumber)
                                                l.Address--;
                                        }
                                        break;
                                    }
                                }
                                else
                                    valueAResult = result.Value;
                            }
                            output[i].Output = output[i].Output.Take(1).ToArray();
                            output[i].Output[0] = (ushort)(value | (valueB << 5) | (valueA << 10));
                            if (valueAResult.HasValue)
                            {
                                if (relocateA)
                                    RelocatedAddresses.Add((ushort)(output[i].Address + 1));
                                output[i].Output = output[i].Output.Concat(new ushort[] { valueAResult.Value }).ToArray();
                            }
                            if (valueBResult.HasValue)
                            {
                                if (relocateB)
                                    RelocatedAddresses.Add((ushort)(output[i].Address + 1 + (valueAResult.HasValue ? 1 : 0)));
                                output[i].Output = output[i].Output.Concat(new ushort[] { valueBResult.Value }).ToArray();
                            }
                        }
                        else if (output[i].CodeType == CodeType.NonBasicInstruction)
                        {
                            byte value = output[i].Opcode.value;
                            byte valueA = 0;
                            if (output[i].ValueA != null)
                                valueA = output[i].ValueA.value;
                            output[i].Output = new ushort[1];
                            if (output[i].ValueA != null)
                            {
                                if (output[i].ValueA.isLiteral) // next-word
                                {
                                    ExpressionResult result = ParseExpression(output[i].ValueA.literal);
                                    if (!result.Successful)
                                    {
                                        output[i].ErrorCode = ErrorCode.IllegalExpression;
                                        continue;
                                    }
                                    output[i].Output = output[i].Output.Concat(new ushort[] { result.Value }).ToArray();
                                }
                            }
                            output[i].Output[0] = (ushort)(value << 5 | (valueA << 10));
                        }
                    }
                }
            }

            return output;
        }

        #endregion

        #region Helper Code

        internal StringMatch MatchString(string value, Dictionary<string, byte> keys)
        {
            value = value.Trim();
            StringMatch match = new StringMatch();
            match.original = value;
            foreach (var opcode in keys)
            {
                int valueIndex = 0;
                bool requiredWhitespaceMet = false;
                bool matchFound = true;
                match.isLiteral = false;
                for (int i = 0; i < opcode.Key.Length && valueIndex < value.Length; i++)
                {
                    match.match = opcode.Key;
                    match.value = opcode.Value;
                    if (opcode.Key[i] == '_') // Required whitespace
                    {
                        if (value[valueIndex] == ' ' || value[valueIndex] == '\t')
                        {
                            requiredWhitespaceMet = true;
                            i--;
                            valueIndex++;
                        }
                        else
                        {
                            if (!requiredWhitespaceMet)
                            {
                                matchFound = false;
                                break;
                            }
                            requiredWhitespaceMet = false;
                        }
                    }
                    else if (opcode.Key[i] == '.') // Optional whitespace
                    {
                        if (value[valueIndex] == ' ' || value[valueIndex] == '\t')
                        {
                            i--;
                            valueIndex++;
                        }
                    }
                    else if (opcode.Key[i] == '%') // value, like A or POP
                    {
                        i++;
                        char valID = opcode.Key[i];
                        int valueStart = valueIndex;
                        if (i == opcode.Key.Length - 1)
                        {
                            valueIndex = value.Length;
                        }
                        else
                        {
                            int delimiter = value.IndexOf(',', valueIndex);
                            if (delimiter == -1)
                            {
                                matchFound = false;
                                break;
                            }
                            else
                                valueIndex = delimiter;
                        }
                        if (valID == 'a')
                            match.valueA = value.Substring(valueStart, valueIndex - valueStart);
                        else
                            match.valueB = value.Substring(valueStart, valueIndex - valueStart);
                    }
                    else if (opcode.Key[i] == '$') // Literal
                    {
                        i++;
                        char valID = opcode.Key[i];
                        int valueStart = valueIndex;
                        if (i == opcode.Key.Length - 1)
                            valueIndex = value.Length;
                        else
                        {
                            int delimiter = value.SafeIndexOf(',', valueIndex);
                            if (delimiter == -1 && opcode.Value != 0x1E)
                                delimiter = value.SafeIndexOfParenthesis('+', valueIndex);
                            if (delimiter == -1)
                                delimiter = value.SafeIndexOf(']', valueIndex);
                            if (delimiter == -1)
                            {
                                matchFound = false;
                                break;
                            }
                            else
                                valueIndex = delimiter;
                        }
                        match.isLiteral = true;
                        match.literal = value.Substring(valueStart, valueIndex - valueStart);
                    }
                    else if (opcode.Key[i] == '&') // Negative literal
                    {
                        i++;
                        char valID = opcode.Key[i];
                        int valueStart = valueIndex;
                        if (i == opcode.Key.Length - 1)
                            valueIndex = value.Length;
                        else
                        {
                            int delimiter = value.SafeIndexOf(',', valueIndex);
                            if (delimiter == -1 && opcode.Value != 0x1E)
                                delimiter = value.SafeIndexOfParenthesis('-', valueIndex);
                            if (delimiter == -1)
                                delimiter = value.SafeIndexOf(']', valueIndex);
                            if (delimiter == -1)
                            {
                                matchFound = false;
                                break;
                            }
                            else
                                valueIndex = delimiter;
                        }
                        match.isLiteral = true;
                        match.literal = "-(" + value.Substring(valueStart, valueIndex - valueStart) + ")";
                    }
                    else
                    {
                        if (value.ToUpper()[valueIndex] != opcode.Key.ToUpper()[i])
                        {
                            matchFound = false;
                            break;
                        }
                        valueIndex++;
                    }
                }
                if (matchFound && valueIndex == value.Length)
                    return match;
            }
            return null;
        }

        private int GetRootNumber(Stack<int> LineNumbers)
        {
            int res = 0;
            foreach (int i in LineNumbers)
                res += i;
            return res;
        }

        public class StringMatch
        {
            public string valueA;
            public string valueB;
            public string match;
            public string original;
            public byte value;
            public bool isLiteral;
            public string literal;
        }

        #endregion
    }
}
