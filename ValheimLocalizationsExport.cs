using UnityEngine;
using BepInEx;
using Newtonsoft.Json;

namespace ValheimLocalizationsExport
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    internal class LocalizationsExport : BaseUnityPlugin
    {
        public const string PluginGUID = "com.ewigl.ValheimLocalizationsExport";
        public const string PluginName = "Valheim Localizations Export";
        public const string PluginVersion = "0.0.1";

        private Type? localizationType;
        private System.Reflection.PropertyInfo? localizationInstanceProperty;
        private System.Reflection.FieldInfo? languagesField;
        private bool hasInitializedLocalizationCache;

        private void Awake()
        {
            if (!TryInitializeLocalizationCache())
            {
                return;
            }

            foreach (var languageName in new[] { "English", "Chinese", "Swedish" })
            {
                TryExportLanguage(languageName);
            }
        }

        private bool TryInitializeLocalizationCache()
        {
            if (hasInitializedLocalizationCache)
            {
                return localizationType != null && localizationInstanceProperty != null;
            }

            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(assembly.GetName().Name, "assembly_guiutils", StringComparison.OrdinalIgnoreCase))
                    {
                        localizationType = assembly.GetType("Localization", false);
                        if (localizationType != null)
                        {
                            localizationInstanceProperty = localizationType.GetProperty("instance", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                            languagesField = localizationType.GetField("m_languages", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            Debug.Log("Localization cache initialized from assembly_guiutils");
                            hasInitializedLocalizationCache = true;
                            return true;
                        }
                    }
                }

                Debug.LogWarning("Localization type not found in assembly_guiutils");
                return false;
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to initialize localization cache: {ex.Message}\n{ex.StackTrace}");
                return false;
            }
        }

        private bool TryExportLanguage(string languageName)
        {
            var translations = GetGameLocalizationByLanguage(languageName);
            if (translations.Count == 0)
            {
                return false;
            }

            ExportTranslationsToJson(translations, $"{languageName}.json");
            Debug.Log($"{languageName} localization exported.");
            return true;
        }

        private IReadOnlyDictionary<string, string> GetGameLocalizationByLanguage(string languageName)
        {
            try
            {
                if (!TryInitializeLocalizationCache())
                {
                    return new Dictionary<string, string>();
                }

                var localizationInstance = localizationInstanceProperty?.GetValue(null);
                if (localizationInstance == null)
                {
                    Debug.LogWarning("Localization instance is not yet initialized");
                    return new Dictionary<string, string>();
                }

                if (TryGetLanguageTranslationsFromLanguagesField(localizationInstance, languageName, out var directTranslations))
                {
                    return directTranslations;
                }

                if (TrySwitchLanguage(localizationInstance, languageName, out var switchedTranslations))
                {
                    return switchedTranslations;
                }

                var currentTranslations = TryGetCurrentTranslations(localizationInstance);
                if (currentTranslations.Count > 0)
                {
                    Debug.LogWarning($"Found current language translations for '{languageName}' only; active game language likely differs.");
                }

                Debug.LogWarning($"Could not find localization data for language '{languageName}' in m_languages or by switching the active language.");
                return new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to get game localization: {ex.Message}\n{ex.StackTrace}");
                return new Dictionary<string, string>();
            }
        }

        private bool TryGetLanguageTranslationsFromLanguagesField(object localizationInstance, string languageName, out IReadOnlyDictionary<string, string> translations)
        {
            translations = new Dictionary<string, string>();

            if (languagesField == null)
            {
                return false;
            }

            var languagesValue = languagesField.GetValue(localizationInstance);
            if (languagesValue == null)
            {
                return false;
            }

            var idict = languagesValue as System.Collections.IDictionary;
            if (idict != null)
            {
                foreach (System.Collections.DictionaryEntry entry in idict)
                {
                    if (entry.Key is string key && string.Equals(key, languageName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (TryConvertToStringDictionary(entry.Value, out var unpacked))
                        {
                            translations = unpacked;
                            Debug.Log($"Found '{languageName}' in m_languages via IDictionary lookup with {unpacked.Count} entries");
                            return true;
                        }
                    }
                }
            }

            var enumerable = languagesValue as System.Collections.IEnumerable;
            if (enumerable != null)
            {
                foreach (var item in enumerable)
                {
                    if (item == null)
                    {
                        continue;
                    }

                    var itemType = item.GetType();
                    var keyProp = itemType.GetProperty("Key", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    var valueProp = itemType.GetProperty("Value", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (keyProp != null && valueProp != null && keyProp.PropertyType == typeof(string))
                    {
                        var key = keyProp.GetValue(item) as string;
                        if (key != null && string.Equals(key, languageName, StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryConvertToStringDictionary(valueProp.GetValue(item), out var unpacked))
                            {
                                translations = unpacked;
                                Debug.Log($"Found '{languageName}' in m_languages via enumerable key-value lookup with {unpacked.Count} entries");
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private bool TrySwitchLanguage(object localizationInstance, string languageName, out IReadOnlyDictionary<string, string> translations)
        {
            translations = new Dictionary<string, string>();

            if (localizationInstance == null)
            {
                return false;
            }

            var localizationType = localizationInstance.GetType();

            foreach (var method in localizationType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                var methodName = method.Name;
                if (methodName.IndexOf("Language", StringComparison.OrdinalIgnoreCase) < 0 && methodName.IndexOf("Load", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    try
                    {
                        var result = method.Invoke(localizationInstance, new object[] { languageName });
                        var activeTranslations = TryGetCurrentTranslations(localizationInstance);
                        if (activeTranslations.Count > 0)
                        {
                            translations = activeTranslations;
                            Debug.Log($"Switched language via method '{method.Name}' and loaded {activeTranslations.Count} translations for '{languageName}'");
                            return true;
                        }

                        if (result is bool boolResult && boolResult)
                        {
                            var retryTranslations = TryGetCurrentTranslations(localizationInstance);
                            if (retryTranslations.Count > 0)
                            {
                                translations = retryTranslations;
                                Debug.Log($"Language switch success via method '{method.Name}' for '{languageName}'");
                                return true;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Language switch method '{method.Name}' failed for '{languageName}': {ex.Message}");
                    }
                }
            }

            foreach (var member in localizationType.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (member.Name.IndexOf("language", StringComparison.OrdinalIgnoreCase) >= 0 && member.FieldType == typeof(string))
                {
                    try
                    {
                        member.SetValue(localizationInstance, languageName);
                        var activeTranslations = TryGetCurrentTranslations(localizationInstance);
                        if (activeTranslations.Count > 0)
                        {
                            translations = activeTranslations;
                            Debug.Log($"Switched language via field '{member.Name}' and loaded {activeTranslations.Count} translations");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Field '{member.Name}' could not be set for '{languageName}': {ex.Message}");
                    }
                }
            }

            foreach (var prop in localizationType.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (prop.Name.IndexOf("language", StringComparison.OrdinalIgnoreCase) >= 0 && prop.CanWrite && prop.PropertyType == typeof(string))
                {
                    try
                    {
                        prop.SetValue(localizationInstance, languageName);
                        var activeTranslations = TryGetCurrentTranslations(localizationInstance);
                        if (activeTranslations.Count > 0)
                        {
                            translations = activeTranslations;
                            Debug.Log($"Switched language via property '{prop.Name}' and loaded {activeTranslations.Count} translations");
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Property '{prop.Name}' could not be set for '{languageName}': {ex.Message}");
                    }
                }
            }

            return false;
        }

        private IReadOnlyDictionary<string, string> TryGetCurrentTranslations(object localizationInstance)
        {
            var translationsFieldInfo = localizationInstance.GetType().GetField("m_translations", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (translationsFieldInfo == null)
            {
                return new Dictionary<string, string>();
            }

            var translationsValue = translationsFieldInfo.GetValue(localizationInstance);
            if (TryConvertToStringDictionary(translationsValue, out var translations))
            {
                return translations;
            }

            return new Dictionary<string, string>();
        }

        private bool TryConvertToStringDictionary(object value, out IReadOnlyDictionary<string, string> translations)
        {
            translations = new Dictionary<string, string>();

            if (value == null)
            {
                return false;
            }

            if (value is IReadOnlyDictionary<string, string>)
            {
                var readOnlyStringDict = (IReadOnlyDictionary<string, string>)value;
                var result = new Dictionary<string, string>();
                foreach (var pair in readOnlyStringDict)
                {
                    result[pair.Key] = pair.Value;
                }

                if (result.Count > 0)
                {
                    translations = result;
                    return true;
                }
            }

            if (value is IDictionary<string, string>)
            {
                var stringDict = (IDictionary<string, string>)value;
                var result = new Dictionary<string, string>();
                foreach (var pair in stringDict)
                {
                    result[pair.Key] = pair.Value;
                }

                if (result.Count > 0)
                {
                    translations = result;
                    return true;
                }
            }

            var dictionary = value as System.Collections.IDictionary;
            if (dictionary != null)
            {
                var converted = new Dictionary<string, string>();
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string key && entry.Value is string text)
                    {
                        converted[key] = text;
                    }
                }

                if (converted.Count > 0)
                {
                    translations = converted;
                    return true;
                }
            }

            return false;
        }

        private bool TryGetCaseInsensitiveKey(System.Collections.IEnumerable keys, string languageName, out string? matchedKey)
        {
            matchedKey = null;
            foreach (var key in keys)
            {
                if (key is string value && string.Equals(value, languageName, StringComparison.OrdinalIgnoreCase))
                {
                    matchedKey = value;
                    return true;
                }
            }

            return false;
        }

        private void ExportTranslationsToJson(IReadOnlyDictionary<string, string> translations, string filename)
        {
            try
            {
                string outputPath = Path.Combine(Paths.PluginPath, "ValheimLocalizationsExport", "Exports", filename);
                string directory = Path.GetDirectoryName(outputPath);

                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                string json = JsonConvert.SerializeObject(translations, Formatting.Indented);
                File.WriteAllText(outputPath, json);
                Debug.Log($"Translations exported to: {outputPath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to export translations: {ex.Message}");
            }
        }
    }
}