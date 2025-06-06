using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace fox_convert
{
    public static class Patchers
    {
        public static string GetExtensionName(string manifestPath)
        {
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException($"Manifest file not found: {manifestPath}");

            string json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("name", out var nameProp))
                return nameProp.GetString() ?? "UnnamedExtension";

            throw new InvalidDataException("Could not find 'name' property in manifest.json");
        }

        public static async Task ConvertManifestAsync(string manifestPath, string extensionDir, Action<string> showWarn)
        {
            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            var manifest = JsonNode.Parse(manifestJson)?.AsObject();
            if (manifest == null) throw new Exception("Failed to parse manifest.json");

            var handlers = new Dictionary<string, Func<JsonNode?, JsonObject, JsonNode?>>
            {
                ["update_url"] = (value, full) => null,
                ["minimum_chrome_version"] = (value, full) => null,
                ["key"] = (value, full) => null,
                ["externally_connectable"] = (value, full) => null,
                ["side_panel"] = (value, full) => null,
                ["storage"] = (value, full) => null,

                ["background"] = (value, full) =>
                {
                    if (value?["page"] is JsonNode pageNode)
                    {
                        var newObj = new JsonObject();
                        newObj["page"] = pageNode.DeepClone();
                        return newObj;
                    }

                    var scriptFiles = new List<string>();
                    if (value?["scripts"] is JsonArray arr)
                    {
                        foreach (var s in arr)
                            if (s != null)
                                scriptFiles.Add(s.ToString());
                    }
                    if (value?["service_worker"] != null)
                        scriptFiles.Add(value["service_worker"]!.ToString());

                    RemoveImportScriptsCalls(scriptFiles, extensionDir, showWarn);

                    var scriptsArray = new JsonArray();
                    foreach (var script in scriptFiles)
                        scriptsArray.Add(JsonValue.Create(script));

                    if (scriptsArray.Count > 0)
                        return new JsonObject { ["scripts"] = scriptsArray };

                    return null;
                },

                ["content_scripts"] = (value, full) =>
                {
                    if (value is JsonArray arr)
                    {
                        foreach (var cs in arr.OfType<JsonObject>())
                        {
                            if (!cs.TryGetPropertyValue("matches", out var matchesNode) || matchesNode is not JsonArray matchesArr || matchesArr.Count == 0)
                            {
                                cs["matches"] = new JsonArray { "<all_urls>" };
                            }
                        }
                    }
                    return value;
                },

                ["permissions"] = (value, full) =>
                {
                    if (value is JsonArray permsArr)
                    {
                        var filtered = new JsonArray();
                        foreach (var p in permsArr)
                        {
                            if (!string.Equals(p?.ToString(), "sidePanel", StringComparison.OrdinalIgnoreCase))
                                filtered.Add(p is null ? null : JsonValue.Create(p.ToString()));
                        }
                        return filtered;
                    }
                    return value;
                },

                ["side_panel"] = (value, full) =>
                {
                    return null;
                }
            };

            int mv = 0;
            if (manifest.TryGetPropertyValue("manifest_version", out var mvNode) && mvNode != null)
            {
                mv = mvNode.GetValue<int>();
            }
            if (mv != 2 && mv != 3)
            {
                mv = CouldBeMV3(manifest) ? 3 : 2;
                manifest["manifest_version"] = mv;
            }
            if (mv == 3)
            {
                if (manifest["permissions"] is JsonArray permsArr)
                {
                    if (!permsArr.Any(p => p?.ToString() == "activeTab"))
                        permsArr.Add("activeTab");
                }
                else
                {
                    manifest["permissions"] = new JsonArray { "activeTab" };
                }

                if (manifest["host_permissions"] is JsonArray hostArr)
                {
                    if (!hostArr.Any(h => h?.ToString() == "<all_urls>"))
                        hostArr.Add("<all_urls>");
                }
                else
                {
                    manifest["host_permissions"] = new JsonArray { "<all_urls>" };
                }
            }
            else if (mv == 2)
            {
                if (manifest["permissions"] is JsonArray permsArr)
                {
                    if (!permsArr.Any(p => p?.ToString() == "activeTab"))
                        permsArr.Add("activeTab");
                    if (!permsArr.Any(p => p?.ToString() == "<all_urls>"))
                        permsArr.Add("<all_urls>");
                }
                else
                {
                    manifest["permissions"] = new JsonArray { "activeTab", "<all_urls>" };
                }
            }

            var firefoxManifest = new JsonObject();
            foreach (var kvp in manifest)
            {
                if (handlers.TryGetValue(kvp.Key, out var handler))
                {
                    var newValue = handler(kvp.Value, manifest);
                    if (newValue != null)
                        firefoxManifest[kvp.Key] = newValue.DeepClone();
                }
                else if (kvp.Value != null)
                {
                    firefoxManifest[kvp.Key] = kvp.Value.DeepClone();
                }
            }

            if (manifest.ContainsKey("side_panel"))
            {
                if (manifest["side_panel"] is JsonObject sidePanel)
                {
                    var defaultPath = sidePanel["default_path"]?.ToString() ?? "sidebar.html";
                    var sidebarAction = new JsonObject
                    {
                        ["default_panel"] = defaultPath,
                        ["default_title"] = manifest.ContainsKey("name") ? manifest["name"]?.ToString() ?? "Sidebar" : "Sidebar"
                    };
                    firefoxManifest["sidebar_action"] = sidebarAction;
                }
            }

            if (!firefoxManifest.ContainsKey("browser_specific_settings"))
            {
                firefoxManifest["browser_specific_settings"] = new JsonObject
                {
                    ["gecko"] = new JsonObject { ["id"] = "halkion@yahoo.com" }
                };
            }

            PatchCspForEval(firefoxManifest);

            await File.WriteAllTextAsync(
                manifestPath,
                firefoxManifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            );
        }

        public static bool CouldBeMV3(JsonObject manifest)
        {
            if (manifest.ContainsKey("host_permissions")) return true;
            if (manifest.ContainsKey("background") && manifest["background"]?["service_worker"] != null) return true;
            if (manifest.ContainsKey("action")) return true;
            if (manifest.ContainsKey("web_accessible_resources"))
            {
                if (manifest["web_accessible_resources"] is JsonArray arr && arr.FirstOrDefault() is JsonObject obj
                    && obj.ContainsKey("resources"))
                    return true;
            }
            if (manifest.ContainsKey("content_security_policy") && manifest["content_security_policy"] is JsonObject)
                return true;
            return false;
        }

        public static void RemoveImportScriptsCalls(IEnumerable<string> scriptFiles, string rootDir, Action<string> showWarn)
        {
            foreach (var script in scriptFiles)
            {
                var fullPath = Path.Combine(rootDir, script);
                if (!File.Exists(fullPath)) continue;

                var content = File.ReadAllText(fullPath);

                var cleaned = Regex.Replace(
                    content,
                    @"importScripts\s*\((.|\s)*?\)\s*;?",
                    String.Empty,
                    RegexOptions.Multiline
                );

                if (content != cleaned)
                {
                    File.WriteAllText(fullPath, cleaned);
                    Console.WriteLine($"Patched importScripts() out of {script}");
                }

                if (Regex.IsMatch(cleaned, @"importScripts\s*\("))
                {
                    showWarn($"importScripts() still present in {script} after patch, manual check recommended.");
                }
            }
        }

        public static void PatchCspForEval(JsonObject manifest)
        {
            if (!manifest.TryGetPropertyValue("content_security_policy", out var cspNode))
            {
                manifest["content_security_policy"] = new JsonObject
                {
                    ["extension_pages"] = "script-src 'self' 'unsafe-eval'; object-src 'self'; style-src 'self' 'unsafe-inline';"
                };
                return;
            }

            string cspValue = string.Empty;
            if (cspNode is JsonValue cspVal && cspVal.TryGetValue<string>(out var cspString))
            {
                cspValue = cspString;
            }
            else if (cspNode is JsonObject cspObj)
            {
                cspValue = cspObj.TryGetPropertyValue("extension_pages", out var ep) ? ep?.ToString() ?? string.Empty : string.Empty;
            }

            cspValue = Regex.Replace(cspValue, @"'?wasm-unsafe-eval'?\s*", string.Empty, RegexOptions.IgnoreCase);
            cspValue = Regex.Replace(cspValue, @"'+unsafe-eval'+", "'unsafe-eval'", RegexOptions.IgnoreCase);

            if (!cspValue.Contains("'unsafe-eval'"))
                cspValue += " 'unsafe-eval'";

            cspValue = Regex.Replace(cspValue, @"\s+", " ").Trim();
            cspValue = cspValue.Trim(';');

            manifest["content_security_policy"] = new JsonObject
            {
                ["extension_pages"] = cspValue
            };
        }

        public static void PatchChromeExtensionUrls(string directory, Action<string> showWarn)
        {
            var reId = new Regex(@"chrome-extension://[^/]+/", RegexOptions.Compiled);

            foreach (var file in Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".js" && ext != ".css" && ext != ".html") continue;

                string result = File.ReadAllText(file).Replace("chrome-extension://", "./");

                result = reId.Replace(result, "./");

                if (result.Contains("chrome-extension://"))
                    showWarn($"Unpatched chrome-extension reference(s) in {file}");
            }
        }

        public static void PatchUnsafeStringMethods(string directory, Action<string> showWarn)
        {
            Regex[] patterns = new[] {
                new Regex(@"(\b(?:var|let|const)?\s*\w+\s*=\s*)([a-zA-Z0-9_$\.]+)\.split\s*\(([^)]*)\)", RegexOptions.Compiled),
                new Regex(@"(\b(?:var|let|const)?\s*\w+\s*=\s*)([a-zA-Z0-9_$\.]+)\.toLowerCase\s*\(\)", RegexOptions.Compiled),
                new Regex(@"(\b(?:var|let|const)?\s*\w+\s*=\s*)([a-zA-Z0-9_$\.]+)\.toUpperCase\s*\(\)", RegexOptions.Compiled),
                new Regex(@"(\b(?:var|let|const)?\s*\w+\s*=\s*)([a-zA-Z0-9_$\.]+)\.trim\s*\(\)", RegexOptions.Compiled),
                new Regex(@"(\b(?:var|let|const)?\s*\w+\s*=\s*)([a-zA-Z0-9_$\.]+)\.replace\s*\(([^)]*)\)", RegexOptions.Compiled),
                new Regex(@"(\b(?:var|let|const)?\s*\w+\s*=\s*)([a-zA-Z0-9_$\.]+)\.substr\s*\(([^)]*)\)", RegexOptions.Compiled),
                new Regex(@"(\b(?:var|let|const)?\s*\w+\s*=\s*)([a-zA-Z0-9_$\.]+)\.substring\s*\(([^)]*)\)", RegexOptions.Compiled),
            };

            var methodHandlers = new Dictionary<string, Func<string, string, string, string>>(StringComparer.Ordinal)
            {
                ["split"] = (left, varName, args) =>
                {
                    if (args.TrimStart().StartsWith("/"))
                        return $"{left}{varName}.split({args})";
                    return $"{left}typeof {varName} === \"string\" ? {varName}.split({args}) : []";
                },
                ["toLowerCase"] = (left, varName, args) =>
                    $"{left}typeof {varName} === \"string\" ? {varName}.toLowerCase() : \"\"",
                ["toUpperCase"] = (left, varName, args) =>
                    $"{left}typeof {varName} === \"string\" ? {varName}.toUpperCase() : \"\"",
                ["trim"] = (left, varName, args) =>
                    $"{left}typeof {varName} === \"string\" ? {varName}.trim() : \"\"",
                ["replace"] = (left, varName, args) =>
                    $"{left}typeof {varName} === \"string\" ? {varName}.replace({args}) : \"\"",
                ["substr"] = (left, varName, args) =>
                    $"{left}typeof {varName} === \"string\" ? {varName}.substr({args}) : \"\"",
                ["substring"] = (left, varName, args) =>
                    $"{left}typeof {varName} === \"string\" ? {varName}.substring({args}) : \"\"",
            };

            Regex methodExtractor = new Regex(@"\.(\w+)", RegexOptions.Compiled);

            foreach (var file in Directory.EnumerateFiles(directory, "*.js", SearchOption.AllDirectories))
            {
                var originalContent = File.ReadAllText(file);
                var content = originalContent;
                foreach (var pattern in patterns)
                {
                    content = pattern.Replace(content, m =>
                    {
                        var left = m.Groups[1].Value;
                        var varName = m.Groups[2].Value;
                        var args = m.Groups.Count > 3 ? m.Groups[3].Value : "";
                        var methodCall = m.Value.Substring(left.Length);
                        var methodMatch = methodExtractor.Match(methodCall);
                        var method = methodMatch.Success ? methodMatch.Groups[1].Value : "";

                        if (methodHandlers.TryGetValue(method, out var handler))
                        {
                            return handler(left, varName, args);
                        }
                        return m.Value;
                    });
                }
                if (content != originalContent)
                    File.WriteAllText(file, content);
            }
        }
    }
}