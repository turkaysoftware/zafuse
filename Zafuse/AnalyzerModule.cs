using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Zafuse{
    internal class AnalyzerModule{
        // Struct to hold counts of dynamic content within strings
        // Used for comparison across translation files
        public struct ContentCounts{
            public int Placeholders; // Count of placeholder tokens like {0}, %s
            public int Punctuation;  // Count of punctuation characters
            public int Quotes;       // Count of single or double quotes
            public int Numbers;      // Count of numeric sequences
            // Override Equals to compare ContentCounts by value
            public override bool Equals(object obj) => obj is ContentCounts other && Placeholders == other.Placeholders && Punctuation == other.Punctuation && Quotes == other.Quotes && Numbers == other.Numbers;
            // Override GetHashCode to match Equals
            public override int GetHashCode(){
                unchecked{
                    int hash = 17;
                    hash = hash * 23 + Placeholders.GetHashCode();
                    hash = hash * 23 + Punctuation.GetHashCode();
                    hash = hash * 23 + Quotes.GetHashCode();
                    hash = hash * 23 + Numbers.GetHashCode();
                    return hash;
                }
            }
        }
        public class TS_FileParser{
            public string Files { get; private set; } // File name without extension
            public Dictionary<string, string> KeySectionMap { get; private set; } // Key - Section mapping
            public Dictionary<string, int> KeyLineMap { get; private set; } // Key - Line number mapping
            public HashSet<string> DuplicateKeys { get; private set; } // Tracks duplicate keys in the same file
            public Dictionary<string, string> KeyValueMap { get; private set; } // Key - Value mapping
            public Dictionary<int, string> Comments { get; private set; } // Line number - comment text
            public int TotalLineCount { get; private set; } // Total number of lines in file
            public TS_FileParser(string filePath){
                Files = Path.GetFileNameWithoutExtension(filePath);
                KeySectionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                KeyLineMap = new Dictionary<string, int>();
                DuplicateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                KeyValueMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Comments = new Dictionary<int, string>();
                ParseFile(filePath);
            }
            private void ParseFile(string filePath){
                if (!File.Exists(filePath)) return;
                string fileContent = File.ReadAllText(filePath, Encoding.UTF8);
                string[] allLines = Regex.Split(fileContent, "\r\n|\r|\n");
                TotalLineCount = allLines.Length;
                string currentSection = "Main";
                for (int i = 0; i < allLines.Length; i++){
                    string line = allLines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Track comment lines
                    if (line.StartsWith(";")) { Comments[i + 1] = line; continue; }
                    // Track section headers [Section]
                    if (line.StartsWith("[") && line.Contains("]")){
                        currentSection = line.Substring(1, line.IndexOf(']') - 1).Trim();
                        continue;
                    }
                    // Track key=value lines
                    if (line.Contains("=")){
                        int equalIdx = line.IndexOf('=');
                        string key = line.Substring(0, equalIdx).Trim();
                        string value = line.Substring(equalIdx + 1).Split(';')[0].Trim(); // remove inline comment
                        string fullKey = $"{currentSection}.{key}";
                        if (KeySectionMap.ContainsKey(fullKey)) DuplicateKeys.Add(fullKey);
                        else{
                            KeySectionMap[fullKey] = currentSection;
                            KeyValueMap[fullKey] = value;
                            KeyLineMap[fullKey] = i + 1;
                        }
                    }
                }
            }
        }
        public class TS_Comparer{
            public List<TS_FileParser> Parsers { get; private set; } // List of loaded file parsers
            public Dictionary<string, HashSet<string>> KeyPresenceMap { get; private set; } // Key - Files that contain it
            public Dictionary<string, Dictionary<string, string>> SectionMismatchMap { get; private set; } // Key - File - Section
            public TS_Comparer(){
                Parsers = new List<TS_FileParser>();
                KeyPresenceMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                SectionMismatchMap = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            }
            // Load all .ini files from folder and build comparison maps
            public void LoadFolder(string folderPath){
                if (!Directory.Exists(folderPath)) return;
                Parsers.Clear(); KeyPresenceMap.Clear(); SectionMismatchMap.Clear();
                foreach (string file in Directory.GetFiles(folderPath, "*.ini")){
                    TS_FileParser parser = new TS_FileParser(file);
                    Parsers.Add(parser);
                    foreach (var kvp in parser.KeySectionMap){
                        if (!KeyPresenceMap.ContainsKey(kvp.Key)) KeyPresenceMap[kvp.Key] = new HashSet<string>();
                        KeyPresenceMap[kvp.Key].Add(parser.Files);
                        string rawKey = kvp.Key.Contains('.') ? kvp.Key.Substring(kvp.Key.IndexOf('.') + 1) : kvp.Key;
                        if (!SectionMismatchMap.ContainsKey(rawKey)) SectionMismatchMap[rawKey] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        SectionMismatchMap[rawKey][parser.Files] = kvp.Value;
                    }
                }
            }
            // Compare placeholders, punctuation, quotes, and numbers across all files
            // Returns only keys with mismatches
            public Dictionary<string, Dictionary<string, ContentCounts>> GetPlaceholderMismatches(bool chkPlc, bool chkPnc, bool chkQt, bool chkNum){
                var result = new Dictionary<string, Dictionary<string, ContentCounts>>();
                var allKeys = Parsers.SelectMany(p => p.KeyValueMap.Keys).Distinct();
                foreach (string key in allKeys){
                    var counts = new Dictionary<string, ContentCounts>();
                    foreach (var parser in Parsers){
                        if (parser.KeyValueMap.TryGetValue(key, out string value)){
                            counts[parser.Files] = GetContentCounts(value, chkPlc, chkPnc, chkQt, chkNum);
                        }
                    }
                    if (counts.Values.Distinct().Count() > 1){
                        result[key] = counts;
                    }
                }
                return result;
            }
            // Regex for detecting placeholders {0}, {name}, etc.
            private static readonly Regex PlaceholderCurly = new Regex(@"\{[^{}]+\}", RegexOptions.Compiled);
            // Regex for detecting printf-style placeholders %s, %d, %1$d, etc.
            private static readonly Regex PlaceholderPercent = new Regex(@"%(\d+\$)?[-+0#]*\d*(\.\d+)?[dfsuxX]", RegexOptions.Compiled);
            // Count placeholders, punctuation, quotes, and numbers in a string
            // Placeholders are removed before counting numbers and punctuation
            private ContentCounts GetContentCounts(string value, bool chkPlc, bool chkPnc, bool chkQt, bool chkNum){
                ContentCounts c = new ContentCounts();
                string temp = value;
                var cM = PlaceholderCurly.Matches(value);   // curly placeholders
                var pM = PlaceholderPercent.Matches(value); // percent placeholders
                if (chkPlc)
                    c.Placeholders = cM.Count + pM.Count;
                // Remove placeholders to avoid double-counting numbers inside them
                foreach (Match m in cM) temp = temp.Replace(m.Value, " ");
                foreach (Match m in pM) temp = temp.Replace(m.Value, " ");
                if (chkPnc)
                    c.Punctuation = temp.Count(ch => new[] { '.', ',', '!', '?', ':', ';' }.Contains(ch));
                if (chkQt)
                    c.Quotes = temp.Count(ch => ch == '\"' || ch == '\'');
                if (chkNum)
                    c.Numbers = Regex.Matches(temp, @"\d+").Count;
                return c;
            }
            // Compare total line counts across files
            public Dictionary<string, int> GetLineCountDifferences(){
                if (Parsers.Count == 0) return new Dictionary<string, int>();
                var lineCounts = Parsers.ToDictionary(p => p.Files, p => p.TotalLineCount);
                int commonCount = lineCounts.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
                return lineCounts.Where(kvp => kvp.Value != commonCount).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
            // Compare comments across files for differences
            public Dictionary<int, Dictionary<string, string>> GetCommentDifferences(){
                var result = new Dictionary<int, Dictionary<string, string>>();
                var allLines = Parsers.SelectMany(p => p.Comments.Keys).Distinct().OrderBy(x => x);
                foreach (var line in allLines){
                    var lineComments = new Dictionary<string, string>();
                    foreach (var parser in Parsers) if (parser.Comments.TryGetValue(line, out var comment)) lineComments[parser.Files] = comment;
                    if (lineComments.Values.Distinct().Count() > 1) result[line] = lineComments;
                }
                return result;
            }
            // Return line numbers of all keys in each file
            public Dictionary<string, Dictionary<string, int>> GetKeyLineNumbers(){
                var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var parser in Parsers){
                    foreach (var kvp in parser.KeyLineMap){
                        if (!result.ContainsKey(kvp.Key)) result[kvp.Key] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        result[kvp.Key][parser.Files] = kvp.Value;
                    }
                }
                return result;
            }
            // Get keys missing in one or more files
            public Dictionary<string, List<string>> GetMissingKeys(){
                var result = new Dictionary<string, List<string>>();
                var allLangs = Parsers.Select(p => p.Files).ToList();
                foreach (var kvp in KeyPresenceMap){
                    var missing = allLangs.Except(kvp.Value).ToList();
                    if (missing.Count > 0) result[kvp.Key] = missing;
                }
                return result;
            }
            // Get duplicate keys per file
            public Dictionary<string, List<string>> GetDuplicateKeys(){
                return Parsers.Where(p => p.DuplicateKeys.Count > 0).ToDictionary(p => p.Files, p => p.DuplicateKeys.ToList());
            }
            // Get section mismatches for keys across files
            public Dictionary<string, Dictionary<string, string>> GetSectionMismatches(){
                var result = new Dictionary<string, Dictionary<string, string>>();
                foreach (var kvp in SectionMismatchMap){
                    if (kvp.Value.Values.Distinct().Count() > 1) result[kvp.Key] = kvp.Value;
                }
                return result;
            }
        }
    }
}