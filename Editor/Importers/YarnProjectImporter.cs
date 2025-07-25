/*
Yarn Spinner is licensed to you under the terms found in the file LICENSE.md.
*/

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using System.Linq;
using Yarn.Compiler;
using System.IO;
using Object = UnityEngine.Object;

#if USE_UNITY_LOCALIZATION
using UnityEditor.Localization;
using UnityEngine.Localization.Tables;
#endif

namespace Yarn.Unity.Editor
{
    [ScriptedImporter(5, new[] { "yarnproject" }, 1000), HelpURL("https://yarnspinner.dev/docs/unity/components/yarn-programs/")]
    [InitializeOnLoad]
    public class YarnProjectImporter : ScriptedImporter
    {
        [System.Serializable]
        public class SerializedDeclaration
        {
            internal static List<Yarn.IType> BuiltInTypesList = new List<Yarn.IType> {
                Yarn.BuiltinTypes.String,
                Yarn.BuiltinTypes.Boolean,
                Yarn.BuiltinTypes.Number,
            };

            public string name = "$variable";

            [UnityEngine.Serialization.FormerlySerializedAs("type")]
            public string typeName = Yarn.BuiltinTypes.String.Name;

            public bool defaultValueBool;
            public float defaultValueNumber;
            public string defaultValueString;

            public string description;

            public bool isImplicit;

            public TextAsset sourceYarnAsset;

            public SerializedDeclaration(Declaration decl)
            {
                this.name = decl.Name;
                this.typeName = decl.Type.Name;
                this.description = decl.Description;
                this.isImplicit = decl.IsImplicit;

                string sourceScriptPath = GetRelativePath(decl.SourceFileName);

                sourceYarnAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(sourceScriptPath);

                if (this.typeName == BuiltinTypes.String.Name)
                {
                    this.defaultValueString = System.Convert.ToString(decl.DefaultValue);
                }
                else if (this.typeName == BuiltinTypes.Boolean.Name)
                {
                    this.defaultValueBool = System.Convert.ToBoolean(decl.DefaultValue);
                }
                else if (this.typeName == BuiltinTypes.Number.Name)
                {
                    this.defaultValueNumber = System.Convert.ToSingle(decl.DefaultValue);
                }
                else
                {
                    throw new System.InvalidOperationException($"Invalid declaration type {decl.Type.Name}");
                }
            }
        }

        public bool useAddressableAssets;

        public static string UnityProjectRootPath => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

#if USE_UNITY_LOCALIZATION
        public bool UseUnityLocalisationSystem = false;
        public StringTableCollection unityLocalisationStringTableCollection;
#endif

        public Project GetProject()
        {
            try
            {
                return Project.LoadFromFile(this.assetPath);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        public ProjectImportData ImportData => AssetDatabase.LoadAssetAtPath<ProjectImportData>(this.assetPath);

        public bool GetProjectReferencesYarnFile(YarnImporter yarnImporter)
        {
            try
            {
                var project = Project.LoadFromFile(this.assetPath);
                var scriptFile = yarnImporter.assetPath;

                var scriptFileWithEnvironmentSeparators = string.Join(System.IO.Path.DirectorySeparatorChar, scriptFile.Split('/'));

                var projectRelativeSourceFiles = project.SourceFiles.Select(GetRelativePath);

                return projectRelativeSourceFiles.Contains(scriptFileWithEnvironmentSeparators);
            }
            catch
            {
                return false;
            }
        }

        public override void OnImportAsset(AssetImportContext ctx)
        {
#if YARNSPINNER_DEBUG
            UnityEngine.Profiling.Profiler.enabled = true;
#endif

            var projectAsset = ScriptableObject.CreateInstance<YarnProject>();

            projectAsset.name = Path.GetFileNameWithoutExtension(ctx.assetPath);

            // Start by creating the asset - no matter what, we need to
            // produce an asset, even if it doesn't contain valid Yarn
            // bytecode, so that other assets don't lose their references.
            ctx.AddObjectToAsset("Project", projectAsset);
            ctx.SetMainObject(projectAsset);

            var importData = ScriptableObject.CreateInstance<ProjectImportData>();
            importData.name = "Project Import Data";
            ctx.AddObjectToAsset("ImportData", importData);

            // Attempt to load the JSON project file.
            Project project;
            try
            {
                project = Yarn.Compiler.Project.LoadFromFile(ctx.assetPath);
            }
            catch (System.Exception)
            {
                var text = File.ReadAllText(ctx.assetPath);
                if (text.StartsWith("title:"))
                {
                    // This is an old-style project that needs to be upgraded.
                    importData.ImportStatus = ProjectImportData.ImportStatusCode.NeedsUpgradeFromV1;

                    // Log to notify the user that this needs to be done.
                    ctx.LogImportError($"Yarn Project {ctx.assetPath} is a version 1 Yarn Project, and needs to be upgraded. Select it in the Inspector, and click Upgrade Yarn project.", this);
                }
                else
                {
                    // We don't know what's going on.
                    importData.ImportStatus = ProjectImportData.ImportStatusCode.Unknown;
                }
                // Either way, we can't continue.
                return;
            }

            importData.sourceFilePaths.AddRange(project.SourceFilePatterns);

            importData.baseLanguageName = project.BaseLanguage;

            foreach (var loc in project.Localisation)
            {
                var hasStringsFile = project.TryGetStringsPath(loc.Key, out var stringsFilePath);
                var hasAssetsFolder = project.TryGetAssetsPath(loc.Key, out var assetsFolderPath);

                var locInfo = new ProjectImportData.LocalizationEntry
                {
                    languageID = loc.Key,
                    stringsFile = hasStringsFile ? AssetDatabase.LoadAssetAtPath<TextAsset>(stringsFilePath) : null,
                    assetsFolder = hasAssetsFolder ? AssetDatabase.LoadAssetAtPath<DefaultAsset>(assetsFolderPath) : null
                };
                importData.localizations.Add(locInfo);
            }

            if (project.Localisation.ContainsKey(project.BaseLanguage) == false)
            {
                importData.localizations.Add(new ProjectImportData.LocalizationEntry
                {
                    languageID = project.BaseLanguage,
                });
            }

            var projectRelativeSourceFiles = project.SourceFiles.Select(GetRelativePath);

            CompilationResult compilationResult;

            if (projectRelativeSourceFiles.Any())
            {
                // This project depends upon this script
                foreach (var scriptPath in projectRelativeSourceFiles)
                {
                    string guid = AssetDatabase.AssetPathToGUID(scriptPath);

                    ctx.DependsOnSourceAsset(scriptPath);

                    importData.yarnFiles.Add(AssetDatabase.LoadAssetAtPath<TextAsset>(scriptPath));

                }

                var library = Actions.GetLibrary();

                // Now to compile the scripts associated with this project.
                var job = CompilationJob.CreateFromFiles(project.SourceFiles);

                job.Library = library;

                try
                {
                    compilationResult = Compiler.Compiler.Compile(job);
                }
                catch (System.Exception e)
                {
                    var errorMessage = $"Encountered an unhandled exception during compilation: {e.Message}";
                    ctx.LogImportError(errorMessage, null);

                    importData.diagnostics.Add(new ProjectImportData.DiagnosticEntry
                    {
                        yarnFile = null,
                        errorMessages = new List<string> { errorMessage },
                    });
                    importData.ImportStatus = ProjectImportData.ImportStatusCode.CompilationFailed;
                    return;
                }

                var errors = compilationResult.Diagnostics.Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error);

                if (errors.Count() > 0)
                {
                    var errorGroups = errors.GroupBy(e => e.FileName);
                    foreach (var errorGroup in errorGroups)
                    {
                        if (errorGroup.Key == null)
                        {
                            // ok so we have no file for some reason
                            // so these are errors currently not tied to a file
                            // so we instead need to just log the errors and move on
                            foreach (var error in errorGroup)
                            {
                                ctx.LogImportError($"Error compiling project: {error.Message}");
                            }

                            importData.diagnostics.Add(new ProjectImportData.DiagnosticEntry
                            {
                                yarnFile = null,
                                errorMessages = errorGroup.Select(e => e.Message).ToList(),
                            });

                            continue;
                        }

                        var relativePath = GetRelativePath(errorGroup.Key);

                        var asset = AssetDatabase.LoadAssetAtPath<Object>(relativePath);

                        foreach (var error in errorGroup)
                        {
                            var relativeErrorFileName = GetRelativePath(error.FileName);
                            ctx.LogImportError($"Error compiling <a href=\"{relativeErrorFileName}\">{relativeErrorFileName}</a> line {error.Range.Start.Line + 1}: {error.Message}", asset);
                        }

                        var fileWithErrors = AssetDatabase.LoadAssetAtPath<TextAsset>(relativePath);

                        // TODO: Associate this compile error to the
                        // corresponding script

                        var errorMessages = errorGroup.Select(e => e.ToString());
                        importData.diagnostics.Add(new ProjectImportData.DiagnosticEntry
                        {
                            yarnFile = fileWithErrors,
                            errorMessages = errorMessages.ToList(),
                        });
                    }
                    importData.ImportStatus = ProjectImportData.ImportStatusCode.CompilationFailed;
                    return;
                }

                if (compilationResult.Program == null)
                {
                    ctx.LogImportError("Internal error: Failed to compile: resulting program was null, but compiler did not report errors.");
                    return;
                }

                importData.containsImplicitLineIDs = compilationResult.ContainsImplicitStringTags;

                // Store _all_ declarations - both the ones in this .yarnproject
                // file, and the ones inside the .yarn files.

                // While we're here, filter out any declarations that begin with
                // our Yarn internal prefix. These are synthesized variables
                // that are generated as a result of the compilation, and are
                // not declared by the user.
                importData.serializedDeclarations = compilationResult.Declarations
                    .Where(decl => !decl.Name.StartsWith("$Yarn.Internal."))
                    .Where(decl => !(decl.Type is FunctionType))
                    .Select(decl => new SerializedDeclaration(decl)).ToList();

#if USE_UNITY_LOCALIZATION
                if (UseUnityLocalisationSystem)
                {
                    AddStringTableEntries(compilationResult, this.unityLocalisationStringTableCollection, project);
                    projectAsset.localizationType = LocalizationType.Unity;
                }
                else
                {
                    CreateYarnInternalLocalizationAssets(ctx, projectAsset, compilationResult, importData);
                    projectAsset.localizationType = LocalizationType.YarnInternal;
                }
#else
                CreateYarnInternalLocalizationAssets(ctx, projectAsset, compilationResult, importData);
                projectAsset.localizationType = LocalizationType.YarnInternal;
#endif

                // Store the compiled program
                byte[] compiledBytes = null;

                using (var memoryStream = new MemoryStream())
                using (var outputStream = new Google.Protobuf.CodedOutputStream(memoryStream))
                {
                    // Serialize the compiled program to memory
                    compilationResult.Program.WriteTo(outputStream);
                    outputStream.Flush();

                    compiledBytes = memoryStream.ToArray();
                }

                projectAsset.compiledYarnProgram = compiledBytes;
            }

            importData.ImportStatus = ProjectImportData.ImportStatusCode.Succeeded;

#if YARNSPINNER_DEBUG
            UnityEngine.Profiling.Profiler.enabled = false;
#endif
        }

        /// <summary>
        /// Checks if the modifications on the Asset Database will necessitate a reimport of the project to stay in sync with the localisation assets.
        /// </summary>
        /// <remarks>
        /// Because assets can be added and removed after associating a folder of assets with a locale modifications won't be detected until runtime when they cause an error.
        /// This is bad for many reasons, so this method will check any modified assets and see if they correspond to this Yarn Project.
        /// If they do it will reimport the project to reassociate them.
        /// </remarks>
        /// <param name="modifiedAssetPaths">The list of asset paths that have been modified, that is to say assets that have been added, removed, or moved.</param>
        public void CheckUpdatedAssetsRequireReimport(List<string> modifiedAssetPaths)
        {
            bool needsReimport = false;

            var comparison = System.StringComparison.CurrentCulture;
            if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor)
            {
                comparison = System.StringComparison.OrdinalIgnoreCase;
            }

            var localeAssetFolderPaths = ImportData.localizations.Where(l => l.assetsFolder != null).Select(l => AssetDatabase.GetAssetPath(l.assetsFolder));
            foreach (var path in localeAssetFolderPaths)
            {
                // we need to ensure we have the trailing seperator otherwise it is to be considered a file
                // and files can never be the parent of another file
                var assetPath = path;
                if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
                {
                    assetPath += Path.DirectorySeparatorChar.ToString();
                }

                foreach (var modified in modifiedAssetPaths)
                {
                    if (modified.StartsWith(assetPath, comparison))
                    {
                        needsReimport = true;
                        goto SHORTCUT;
                    }
                }
            }

        SHORTCUT:
            if (needsReimport)
            {
                AssetDatabase.ImportAsset(this.assetPath);
            }
        }

        internal static string GetRelativePath(string path)
        {
            if (path.StartsWith(UnityProjectRootPath) == false)
            {
                // This is not a child of the current project. If it's an
                // absolute path, then it's enough to go on.
                if (Path.IsPathRooted(path))
                {
                    return path;
                }
                else
                {
                    throw new System.ArgumentException($"Path {path} is not a child of the project root path {UnityProjectRootPath}");
                }
            }
            // Trim the root path off along with the trailing slash
            return path.Substring(UnityProjectRootPath.Length + 1);
        }

        private void CreateYarnInternalLocalizationAssets(AssetImportContext ctx, YarnProject projectAsset, CompilationResult compilationResult, ProjectImportData importData)
        {
            // Will we need to create a default localization? This variable
            // will be set to false if any of the languages we've
            // configured in languagesToSourceAssets is the default
            // language.
            var shouldAddDefaultLocalization = true;

            foreach (var localisationInfo in importData.localizations)
            {
                // Don't create a localization if the language ID was not
                // provided
                if (string.IsNullOrEmpty(localisationInfo.languageID))
                {
                    Debug.LogWarning($"Not creating a localization for {projectAsset.name} because the language ID wasn't provided.");
                    continue;
                }

                IEnumerable<StringTableEntry> stringTable;

                // Where do we get our strings from? If it's the default
                // language, we'll pull it from the scripts. If it's from
                // any other source, we'll pull it from the CSVs.
                if (localisationInfo.languageID == importData.baseLanguageName)
                {
                    // No strings file needed - we'll use the program-supplied string table.
                    stringTable = GenerateStringsTable();

                    // We don't need to add a default localization.
                    shouldAddDefaultLocalization = false;
                }
                else
                {
                    // No strings file provided
                    if (localisationInfo.stringsFile == null)
                    {
                        Debug.LogWarning($"Not creating a localisation for {localisationInfo.languageID} in the Yarn project {projectAsset.name} because a strings file was not specified, and {localisationInfo.languageID} is not the project's base language");
                        continue;
                    }
                    try
                    {
                        stringTable = StringTableEntry.ParseFromCSV(localisationInfo.stringsFile.text);
                    }
                    catch (System.ArgumentException e)
                    {
                        Debug.LogWarning($"Not creating a localization for {localisationInfo.languageID} in the Yarn Project {projectAsset.name} because an error was encountered during text parsing: {e}");
                        continue;
                    }
                }

                var newLocalization = ScriptableObject.CreateInstance<Localization>();
                newLocalization.LocaleCode = localisationInfo.languageID;

                // Add these new lines to the localisation's asset
                foreach (var entry in stringTable)
                {
                    newLocalization.AddLocalisedStringToAsset(entry.ID, entry.Text);
                }

                projectAsset.localizations.Add(newLocalization);
                newLocalization.name = localisationInfo.languageID;

                if (localisationInfo.assetsFolder != null)
                {
                    newLocalization.ContainsLocalizedAssets = true;

#if USE_ADDRESSABLES
                    const bool addressablesAvailable = true;
#else
                    const bool addressablesAvailable = false;
#endif

                    if (addressablesAvailable && useAddressableAssets)
                    {
                        // We only need to flag that the assets
                        // required by this localization are accessed
                        // via the Addressables system. (Call
                        // YarnProjectUtility.UpdateAssetAddresses to
                        // ensure that the appropriate assets have the
                        // appropriate addresses.)
                        newLocalization.UsesAddressableAssets = true;
                    }
                    else
                    {
                        // We need to find the assets used by this
                        // localization now, and assign them to the
                        // Localization object.
#if YARNSPINNER_DEBUG
                        // This can take some time, so we'll measure
                        // how long it takes.
                        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
#endif

                        // Get the line IDs.
                        IEnumerable<string> lineIDs = stringTable.Select(s => s.ID);

                        // Map each line ID to its asset path.
                        var stringIDsToAssetPaths = YarnProjectUtility.FindAssetPathsForLineIDs(lineIDs, AssetDatabase.GetAssetPath(localisationInfo.assetsFolder));

                        // Load the asset, so we can assign the reference.
                        var assetPaths = stringIDsToAssetPaths
                            .Select(a => new KeyValuePair<string, Object>(a.Key, AssetDatabase.LoadAssetAtPath<Object>(a.Value)));

                        newLocalization.AddLocalizedObjects(assetPaths);

#if YARNSPINNER_DEBUG
                        stopwatch.Stop();
                        Debug.Log($"Imported {stringIDsToAssetPaths.Count()} assets for {project.name} \"{pair.languageID}\" in {stopwatch.ElapsedMilliseconds}ms");
#endif
                    }

                }

                ctx.AddObjectToAsset("localization-" + localisationInfo.languageID, newLocalization);

                if (localisationInfo.languageID == importData.baseLanguageName)
                {
                    // If this is our default language, set it as such
                    projectAsset.baseLocalization = newLocalization;

                    // Since this is the default language, also populate the line metadata.
                    projectAsset.lineMetadata = new LineMetadata(LineMetadataTableEntriesFromCompilationResult(compilationResult));
                }
                else if (localisationInfo.stringsFile != null)
                {
                    // This localization depends upon a source asset. Make
                    // this asset get re-imported if this source asset was
                    // modified
                    ctx.DependsOnSourceAsset(AssetDatabase.GetAssetPath(localisationInfo.stringsFile));
                }
            }

            if (shouldAddDefaultLocalization)
            {
                // We didn't add a localization for the default language.
                // Create one for it now.
                var stringTableEntries = GetStringTableEntries(compilationResult);

                var developmentLocalization = ScriptableObject.CreateInstance<Localization>();
                developmentLocalization.name = $"Default ({importData.baseLanguageName})";
                developmentLocalization.LocaleCode = importData.baseLanguageName;


                // Add these new lines to the development localisation's asset
                foreach (var entry in stringTableEntries)
                {
                    developmentLocalization.AddLocalisedStringToAsset(entry.ID, entry.Text);
                }

                projectAsset.baseLocalization = developmentLocalization;
                projectAsset.localizations.Add(projectAsset.baseLocalization);
                ctx.AddObjectToAsset("default-language", developmentLocalization);

                // Since this is the default language, also populate the line metadata.
                projectAsset.lineMetadata = new LineMetadata(LineMetadataTableEntriesFromCompilationResult(compilationResult));
            }
        }

#if USE_UNITY_LOCALIZATION
        private void AddStringTableEntries(CompilationResult compilationResult, StringTableCollection unityLocalisationStringTableCollection, Yarn.Compiler.Project project)
        {
            if (unityLocalisationStringTableCollection == null)
            {
                Debug.LogError("Unable to generate String Table Entries as the string collection is null");
                return;
            }

            // Get the Unity string table corresponding to the Yarn Project's
            // base language. If a table can't be found for the language but can
            // be for the language's parent, use that. Otherwise, return null.
            StringTable FindBaseLanguageStringTable()
            {
                StringTable baseLanguageStringTable = unityLocalisationStringTableCollection.StringTables
                    .FirstOrDefault(t => t.LocaleIdentifier == project.BaseLanguage);

                if (baseLanguageStringTable != null)
                {
                    // We found a table that matches the project's language!
                    return baseLanguageStringTable;
                }

                // We didn't find a string table that exactly matches the locale
                // code of our Yarn Project's base language. Maybe we can try to
                // find a string table for our base language's parent.

                System.Globalization.CultureInfo defaultCulture = null;
                try
                {
                    defaultCulture = new System.Globalization.CultureInfo(project.BaseLanguage);
                }
                catch (System.Globalization.CultureNotFoundException)
                {
                    // We can't find a CultureInfo for the base language.
                    return null;
                }

                if (defaultCulture.IsNeutralCulture)
                {
                    // The base language is a neutral culture. It has no parent
                    // we could look for.
                    return null;
                }

                var defaultNeutralCulture = defaultCulture.Parent;

                var defaultNeutralStringTable = unityLocalisationStringTableCollection.StringTables.FirstOrDefault(table => table.LocaleIdentifier == defaultNeutralCulture.Name);

                return defaultNeutralStringTable;
            }

            var unityStringTable = FindBaseLanguageStringTable();

            if (unityStringTable == null)
            {
                Debug.LogWarning($"Unable to find a locale in the string table that matches the default locale {project.BaseLanguage}");
                return;
            }

            foreach (var yarnEntry in compilationResult.StringTable)
            {
                // Grab the data that we'll put in the string table
                var lineID = yarnEntry.Key;
                var stringInfo = yarnEntry.Value;

                // Do we already have an entry with this line ID?
                UnityEngine.Localization.Tables.StringTableEntry unityEntry = unityStringTable.GetEntry(lineID);

                if (unityEntry != null)
                {
                    // We have an existing entry, so update it.
                    unityEntry.Value = stringInfo.text;
                }
                else
                {
                    // Create a new entry for this content.
                    unityEntry = unityStringTable.AddEntry(lineID, stringInfo.text);
                }

                // Next, set up the metadata on this entry. We'll start by
                // getting the list of hashtags on the line, not including its
                // line ID (we don't need it in metadata, because it's already
                // stored as the table entry's key.)
                var tags = RemoveLineIDFromMetadata(stringInfo.metadata).ToArray();

                // this allows us to reference the original CharacterName from the Line, ignoring its translation.
                if (stringInfo.text.Contains(':'))
                {
                    string speaker = stringInfo.text[..stringInfo.text.IndexOf(':')].Trim().ToLower();
                    Array.Resize(ref tags, tags.Length + 1);
                    tags[^1] = $"speaker:{speaker}";
                }

                // Next, do we already have metadata for the Unity table entry?
                var existingSharedMetadata = unityEntry.SharedEntry.Metadata.GetMetadata<UnityLocalization.LineMetadata>();

                if (existingSharedMetadata != null)
                {
                    // We do. Update the existing metadata.
                    existingSharedMetadata.nodeName = stringInfo.nodeName;
                    existingSharedMetadata.tags = tags;
                }
                else
                {
                    // Create a new metadata.
                    unityEntry.SharedEntry.Metadata.AddMetadata(new UnityLocalization.LineMetadata
                    {
                        nodeName = stringInfo.nodeName,
                        tags = tags,
                    });
                }
            }

            // We've made changes to the table, so flag it and its shared data
            // as dirty.
            EditorUtility.SetDirty(unityStringTable);
            EditorUtility.SetDirty(unityStringTable.SharedData);
            return;

        }
#endif

        /// <summary>
        /// Gets a value indicating whether this Yarn Project contains any
        /// compile errors.
        /// </summary>
        internal bool HasErrors
        {
            get
            {
                var importData = AssetDatabase.LoadAssetAtPath<ProjectImportData>(this.assetPath);

                if (importData == null)
                {
                    // If we have no import data, then a problem has occurred
                    // when importing this project, so indicate 'true' as
                    // signal.
                    return true;
                }
                return importData.HasCompileErrors;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this Yarn Project is able to
        /// generate a strings table - that is, it has no compile errors,
        /// it has at least one script, and all scripts are fully tagged.
        /// </summary>
        /// <inheritdoc path="exception"
        /// cref="GetScriptHasLineTags(TextAsset)"/>
        internal bool CanGenerateStringsTable
        {
            get
            {
                var importData = AssetDatabase.LoadAssetAtPath<ProjectImportData>(this.assetPath);

                if (importData == null)
                {
                    return false;
                }

                return importData.HasCompileErrors == false && importData.containsImplicitLineIDs == false;
            }
        }

        private CompilationResult? CompileStringsOnly()
        {
            var paths = GetProject().SourceFiles;

            var job = CompilationJob.CreateFromFiles(paths);
            job.CompilationType = CompilationJob.Type.StringsOnly;

            return Compiler.Compiler.Compile(job);
        }

        internal IEnumerable<string> GetErrorsForScript(TextAsset sourceScript)
        {
            if (ImportData == null)
            {
                return Enumerable.Empty<string>();
            }
            foreach (var errorCollection in ImportData.diagnostics)
            {
                if (errorCollection.yarnFile == sourceScript)
                {
                    return errorCollection.errorMessages;
                }
            }
            return Enumerable.Empty<string>();
        }

        /// <summary>
        /// Generates a collection of <see cref="StringTableEntry"/>
        /// objects, one for each line in this Yarn Project's scripts.
        /// </summary>
        /// <returns>An IEnumerable containing a <see
        /// cref="StringTableEntry"/> for each of the lines in the Yarn
        /// Project, or <see langword="null"/> if the Yarn Project contains
        /// errors.</returns>
        public IEnumerable<StringTableEntry> GenerateStringsTable()
        {
            CompilationResult? compilationResult = CompileStringsOnly();

            if (!compilationResult.HasValue)
            {
                // We only get no value if we have no scripts to work with.
                // In this case, return an empty collection - there's no
                // error, but there's no content either.
                return new List<StringTableEntry>();
            }

            var errors = compilationResult.Value.Diagnostics.Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error);

            if (errors.Count() > 0)
            {
                Debug.LogError($"Can't generate a strings table from a Yarn Project that contains compile errors", null);
                return null;
            }

            return GetStringTableEntries(compilationResult.Value);
        }

        internal IEnumerable<LineMetadataTableEntry> GenerateLineMetadataEntries()
        {
            CompilationResult? compilationResult = CompileStringsOnly();

            if (!compilationResult.HasValue)
            {
                // We only get no value if we have no scripts to work with.
                // In this case, return an empty collection - there's no
                // error, but there's no content either.
                return new List<LineMetadataTableEntry>();
            }

            var errors = compilationResult.Value.Diagnostics.Where(d => d.Severity == Diagnostic.DiagnosticSeverity.Error);

            if (errors.Count() > 0)
            {
                Debug.LogError($"Can't generate line metadata entries from a Yarn Project that contains compile errors", null);
                return null;
            }

            return LineMetadataTableEntriesFromCompilationResult(compilationResult.Value);
        }

        private IEnumerable<StringTableEntry> GetStringTableEntries(CompilationResult result)
        {

            return result.StringTable.Select(x => new StringTableEntry
            {
                ID = x.Key,
                Language = GetProject().BaseLanguage,
                Text = x.Value.text,
                File = x.Value.fileName,
                Node = x.Value.nodeName,
                LineNumber = x.Value.lineNumber.ToString(),
                Lock = YarnImporter.GetHashString(x.Value.text, 8),
                Comment = GenerateCommentWithLineMetadata(x.Value.metadata),
            });
        }

        private IEnumerable<LineMetadataTableEntry> LineMetadataTableEntriesFromCompilationResult(CompilationResult result)
        {
            return result.StringTable.Select(x => new LineMetadataTableEntry
            {
                ID = x.Key,
                File = x.Value.fileName,
                Node = x.Value.nodeName,
                LineNumber = x.Value.lineNumber.ToString(),
                Metadata = RemoveLineIDFromMetadata(x.Value.metadata).ToArray(),
            }).Where(x => x.Metadata.Length > 0);
        }

        /// <summary>
        /// Generates a string with the line metadata. This string is intended
        /// to be used in the "comment" column of a strings table CSV. Because
        /// of this, it will ignore the line ID if it exists (which is also
        /// part of the line metadata).
        /// </summary>
        /// <param name="metadata">The metadata from a given line.</param>
        /// <returns>A string prefixed with "Line metadata: ", followed by each
        /// piece of metadata separated by whitespace. If no metadata exists or
        /// only the line ID is part of the metadata, returns an empty string
        /// instead.</returns>
        private string GenerateCommentWithLineMetadata(string[] metadata)
        {
            var cleanedMetadata = RemoveLineIDFromMetadata(metadata);

            if (cleanedMetadata.Count() == 0)
            {
                return string.Empty;
            }

            return $"Line metadata: {string.Join(" ", cleanedMetadata)}";
        }

        /// <summary>
        /// Removes any line ID entry from an array of line metadata.
        /// Line metadata will always contain a line ID entry if it's set. For
        /// example, if a line contains "#line:1eaf1e55", its line metadata
        /// will always have an entry with "line:1eaf1e55".
        /// </summary>
        /// <param name="metadata">The array with line metadata.</param>
        /// <returns>An IEnumerable with any line ID entries removed.</returns>
        private IEnumerable<string> RemoveLineIDFromMetadata(string[] metadata)
        {
            return metadata.Where(x => !x.StartsWith("line:"));
        }
        public const string UnityProjectRootVariable = "${UnityProjectRoot}";
    }

    public static class ProjectExtensions
    {

        public static bool TryGetStringsPath(this Yarn.Compiler.Project project, string languageCode, out string fullStringsPath)
        {
            if (project.Localisation.TryGetValue(languageCode, out var info) == false)
            {
                fullStringsPath = default;
                return false;
            }
            if (string.IsNullOrEmpty(info.Strings))
            {
                fullStringsPath = default;
                return false;
            }

            var projectFolderRelative = Path.GetDirectoryName(project.Path);
            var projectFolderAbsolute = Path.GetFullPath(Path.Combine(YarnProjectImporter.UnityProjectRootPath, projectFolderRelative));

            var expandedPath = info.Strings.Replace(YarnProjectImporter.UnityProjectRootVariable, YarnProjectImporter.UnityProjectRootPath);

            if (Path.IsPathRooted(expandedPath) == false)
            {
                expandedPath = Path.GetFullPath(Path.Combine(projectFolderAbsolute, expandedPath));
            }

            fullStringsPath = YarnProjectImporter.GetRelativePath(expandedPath);

            return true;
        }

        public static bool TryGetAssetsPath(this Yarn.Compiler.Project project, string languageCode, out string fullAssetsPath)
        {
            if (project.Localisation.TryGetValue(languageCode, out var info) == false)
            {
                fullAssetsPath = default;
                return false;
            }
            if (string.IsNullOrEmpty(info.Assets))
            {
                fullAssetsPath = default;
                return false;
            }
            var projectFolderRelative = Path.GetDirectoryName(project.Path);
            var projectFolderAbsolute = Path.GetFullPath(Path.Combine(YarnProjectImporter.UnityProjectRootPath, projectFolderRelative));

            var expandedPath = info.Assets.Replace(YarnProjectImporter.UnityProjectRootVariable, YarnProjectImporter.UnityProjectRootPath);

            if (Path.IsPathRooted(expandedPath) == false)
            {
                expandedPath = Path.GetFullPath(Path.Combine(projectFolderAbsolute, expandedPath));
            }

            fullAssetsPath = YarnProjectImporter.GetRelativePath(expandedPath);

            return true;
        }
    }
}
