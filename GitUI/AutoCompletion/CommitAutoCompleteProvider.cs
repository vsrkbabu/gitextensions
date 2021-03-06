using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GitCommands;
using JetBrains.Annotations;
using Microsoft.VisualStudio.Threading;

namespace GitUI.AutoCompletion
{
    public class CommitAutoCompleteProvider : IAutoCompleteProvider
    {
        private static readonly Lazy<Dictionary<string, Regex>> _regexes = new Lazy<Dictionary<string, Regex>>(ParseRegexes);
        private readonly GitModule _module;

        public CommitAutoCompleteProvider(GitModule module)
        {
            _module = module;
        }

        public async Task<IEnumerable<AutoCompleteWord>> GetAutoCompleteWordsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var autoCompleteWords = new HashSet<string>();

            var cmd = GitCommandHelpers.GetAllChangedFilesCmd(true, UntrackedFilesMode.Default, noLocks: true);
            var output = await _module.GitExecutable.GetOutputAsync(cmd).ConfigureAwait(false);
            var changedFiles = GitCommandHelpers.GetStatusChangedFilesFromString(_module, output);
            foreach (var file in changedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var regex = GetRegexForExtension(Path.GetExtension(file.Name));

                if (regex != null)
                {
                    var text = await GetChangedFileTextAsync(_module, file);
                    var matches = regex.Matches(text);
                    foreach (Match match in matches)
                    {
                        // Skip first group since it always contains the entire matched string (regardless of capture groups)
                        foreach (Group group in match.Groups.OfType<Group>().Skip(1))
                        {
                            foreach (Capture capture in group.Captures)
                            {
                                autoCompleteWords.Add(capture.Value);
                            }
                        }
                    }
                }

                autoCompleteWords.Add(Path.GetFileNameWithoutExtension(file.Name));
                autoCompleteWords.Add(Path.GetFileName(file.Name));
                if (!string.IsNullOrWhiteSpace(file.OldName))
                {
                    autoCompleteWords.Add(Path.GetFileNameWithoutExtension(file.OldName));
                    autoCompleteWords.Add(Path.GetFileName(file.OldName));
                }
            }

            return autoCompleteWords.Select(w => new AutoCompleteWord(w));
        }

        [CanBeNull]
        private static Regex GetRegexForExtension(string extension)
        {
            return _regexes.Value.ContainsKey(extension) ? _regexes.Value[extension] : null;
        }

        private static IEnumerable<string> ReadOrInitializeAutoCompleteRegexes()
        {
            var path = Path.Combine(AppSettings.ApplicationDataPath.Value, "AutoCompleteRegexes.txt");

            if (File.Exists(path))
            {
                return File.ReadLines(path);
            }

            Stream s = Assembly.GetEntryAssembly()?.GetManifestResourceStream("GitExtensions.AutoCompleteRegexes.txt");
            if (s == null)
            {
                throw new NotImplementedException("Please add AutoCompleteRegexes.txt file into .csproj");
            }

            using (var sr = new StreamReader(s))
            {
                return sr.ReadToEnd().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        private static Dictionary<string, Regex> ParseRegexes()
        {
            var autoCompleteRegexes = ReadOrInitializeAutoCompleteRegexes();

            var regexes = new Dictionary<string, Regex>();

            foreach (var line in autoCompleteRegexes)
            {
                var i = line.IndexOf('=');
                var extensionStr = line.Substring(0, i);
                var regexStr = line.Substring(i + 1).Trim();

                var extensions = extensionStr.Split(',').Select(s => s.Trim()).Distinct();
                var regex = new Regex(regexStr, RegexOptions.Compiled);

                foreach (var extension in extensions)
                {
                    regexes.Add(extension, regex);
                }
            }

            return regexes;
        }

        [CanBeNull]
        private static async Task<string> GetChangedFileTextAsync(GitModule module, GitItemStatus file)
        {
            var changes = await module.GetCurrentChangesAsync(file.Name, file.OldName, file.Staged == StagedStatus.Index, "-U1000000")
                .ConfigureAwait(false);

            if (changes != null)
            {
                return changes.Text;
            }

            var content = await module.GetFileContentsAsync(file).ConfigureAwaitRunInline();

            if (content != null)
            {
                return content;
            }

            // Try to read the contents of the file: if it cannot be read, skip the operation silently.
            try
            {
                using (var reader = File.OpenText(Path.Combine(module.WorkingDir, file.Name)))
                {
                    return await reader.ReadToEndAsync();
                }
            }
            catch
            {
                return "";
            }
        }
    }
}
