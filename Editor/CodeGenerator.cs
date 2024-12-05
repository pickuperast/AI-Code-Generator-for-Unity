// Copyright (c) Sanat. All rights reserved.
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Sanat.ApiGemini;
using Sanat.ApiOpenAI;
using Sanat.CodeGenerator.Agents;
using Sanat.CodeGenerator.Bookmarks;
using Sanat.CodeGenerator.CodebaseRag;
using Sanat.CodeGenerator.Editor;
using Sanat.CodeGenerator.Extensions;

namespace Sanat.CodeGenerator
{
    public class CodeGenerator : EditorWindow
    {
        #region Fields
        private CodeGeneratorSettingsManager _settingsManager;
        private CodeGeneratorUIRenderer _uiRenderer;
        private CodeGeneratorPreparationHelper _preparationHelper;
        private RagProcessor _ragProcessor; 

        // UI State
        public Vector2 scrollPosition;
        public Vector2 taskScrollPosition;
        public string classNameInput = "";
        public string taskInput = "";
        public bool isSettingsVisible;
        public bool isAgentSettingsVisible;
        public Dictionary<string, AgentModelSettings> agentModelSettings = new();
        public bool IsSettingsLoaded;

        // API Keys/Settings  
        public string geminiApiKey, openaiApiKey, antrophicApiKey, groqApiKey;
        public string supabaseUrl, supabaseKey;
        public string tableName;
        public bool isRag;
        public string GeminiProjectName;

        // Generation State
        public string generatedPrompt = "";
        public bool isGeneratingCode;
        public float generationProgress;
        private float lastProgressUpdateTime;
        private float targetProgress;
        private float progressSpeed = 0.00001f;
        private const int MAX_ALLOWED_VALIDITY_CHECKINGS = 3;
        private int _currentRun;

        // Button Animation
        private float buttonAnimationTime = 1f;
        private float buttonAnimationProgress;
        public bool isButtonAnimating;
        public Color buttonColor = Color.green;

        // Project Data
        public List<string> selectedClassNames = new();
        public Dictionary<string, string> classToPath = new();
        public List<string> _ignoredFolders = new();
        public List<IncludedFolder> includedFolders = new();
        public static IncludedFoldersManager IncludedFoldersManager;

        // Constants
        private const string PLUGIN_NAME = "Code Generator ";
        private const string PROMPTS_SAVE_FOLDER = "Sanat/CodeGenerator/Prompts";
        public const string PREFS_GEMINI_PROJECT_NAME = "GeminiProjectName";
        public const string PREFS_KEY_TASK = "Task";
        public const string INCLUDED_FOLDERS_PREFS_KEY = "IncludedFolders";

        // Managers
        public CodeGeneratorBookmarks bookmarkManager;
        #endregion
        public RagProcessor ragProcessor;

        [System.Serializable]
        public class IncludedFolder
        {
            public string path;
            public bool isEnabled;
        }

        [MenuItem("Tools/Sanat/CodeGenerator")]
        public static void ShowWindow() => GetWindow<CodeGenerator>("Code Generator");

        private async void OnEnable()
        {
            InitializeManagers();
            // if (IsFirstLaunch())
                await LoadSettingsDelayedAsync();
            // else
            //     _settingsManager.LoadSettings(this);
        }
        
        private async UniTask LoadSettingsDelayedAsync()
        {
            await UniTask.Delay(System.TimeSpan.FromSeconds(5), DelayType.DeltaTime, PlayerLoopTiming.Update);
            _settingsManager.LoadSettings(this);
        }
        
        private void OnGUI() => _uiRenderer.RenderMainUI(this);

        private void OnDisable() => _settingsManager.SaveSettings(this);
        
        private void InitializeManagers()
        {
            _settingsManager = new CodeGeneratorSettingsManager();
            _uiRenderer = new CodeGeneratorUIRenderer();
            _preparationHelper = new CodeGeneratorPreparationHelper();
            _ragProcessor = new RagProcessor();
        }
        
        private async void ProcessAllFilesForRag()
        {
            string projectPath = Application.dataPath;
            EditorUtility.DisplayProgressBar("Processing Files", "Starting...", 0f);

            try
            {
                await ragProcessor.ProcessAllFiles(projectPath);
                EditorUtility.DisplayDialog("RAG Processing Complete", "All files have been processed for RAG.", "OK");
            }
            catch (Exception e)
            {
                Debug.LogError($"Error processing files for RAG: {e.Message}");
                EditorUtility.DisplayDialog("Error", $"An error occurred while processing files for RAG: {e.Message}", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        #region Code Generation
        public void ExecGeneratePrompt()
        {
            Dictionary<string, string> projectCode = _preparationHelper.PrepareProjectCode(selectedClassNames, classToPath, _ignoredFolders);
            SaveTaskToPrefs();
            GeneratePrompt(projectCode, 3);
            SavePromptToFile();
            CopyPromptToClipboard();
            StartButtonAnimation();
        }

        private void SaveTaskToPrefs() => EditorPrefs.SetString(PREFS_KEY_TASK, taskInput);

        public void ExecGenerateCode()
        {
            Dictionary<string, string> projectCode = _preparationHelper.PrepareProjectCode(selectedClassNames, classToPath, _ignoredFolders);
            InitiateGeneration(projectCode);
        }

        private void InitiateGeneration(Dictionary<string, string> projectCode)
        {
            isGeneratingCode = true;
            _currentRun = 0;
            generationProgress = 0f;
            targetProgress = .33f;
            lastProgressUpdateTime = (float)EditorApplication.timeSinceStartup;
            EditorApplication.update += UpdateProgressBar;
            SaveTaskToPrefs();
            string[] projectCodePathes = projectCode.Keys.ToArray();
            GeneratePrompt(projectCode);
            SavePromptToFile();
            CopyPromptToClipboard();
            StartButtonAnimation();
            var includedCode = _preparationHelper.GenerateIncludedCode(projectCode, "");
            InitiateCodeGeneration(projectCodePathes, includedCode);
        }

        private void InitiateCodeGeneration(string[] projectCodePaths, string includedCode)
        {
            var apiKeys = new AbstractAgentHandler.ApiKeys(openaiApiKey, antrophicApiKey, groqApiKey, geminiApiKey);
    
            // Initialize agents with appropriate settings
            var agentCodeArchitector = new AgentCodeArchitector(apiKeys, taskInput, includedCode);
            var architectorSettings = agentModelSettings["AgentCodeArchitector"];
            agentCodeArchitector.ChangeLLM(architectorSettings.ApiProvider, architectorSettings.ModelName);
    
            // Convert paths to dictionary for project code
            Dictionary<string, string> projectCode = new Dictionary<string, string>();
            foreach(var path in projectCodePaths)
            {
                if(File.Exists(path))
                {
                    projectCode[path] = File.ReadAllText(path);
                }
            }
    
            var agentSolutionToDicts = new AgentSolutionToDict(apiKeys, projectCodePaths);
            var agentCodeMerger = new AgentCodeMerger(apiKeys, projectCode); // Using the correct constructor

            // Set up callbacks and chain
            SetupArchitectorCallbacks(agentCodeArchitector, agentCodeMerger, apiKeys, agentSolutionToDicts, includedCode);
    
            // Start the generation process with initial prompt
            agentCodeArchitector.SetNext(null);
            agentCodeArchitector.Handle(generatedPrompt);
        }
        #endregion

        private void SetupArchitectorCallbacks(
            AgentCodeArchitector agentCodeArchitector,
            AgentCodeMerger agentCodeMerger, 
            AbstractAgentHandler.ApiKeys apiKeys,
            AbstractAgentHandler agentSolutionToDicts,
            string includedCode)
        {
            // Configure architect's completion handling
            agentCodeArchitector.OnComplete += (string result) =>
            {
                // Create validator to check architect's output
                AgentCodeValidator agentValidator = new AgentCodeValidator(apiKeys, taskInput, includedCode, result);
                
                agentValidator.OnComplete += (string validationResult) =>
                {
                    NextStepsAfterArchitector(validationResult, agentCodeMerger, apiKeys, result, 
                        agentSolutionToDicts, agentCodeArchitector, includedCode);
                };

                // Start validation
                agentValidator.Handle(result);
            };

            // Configure merger completion to finish the process
            agentCodeMerger.OnComplete += (string mergedCode) => 
            { 
                Debug.Log($"Code generation completed. Result: {mergedCode}");
                FinishGenerationActions();
            };

            // Handle potential failures 
            agentCodeArchitector.OnUnsuccessfull += () =>
            {
                Debug.LogError("Code architect failed to generate solution");
                FinishGenerationActions();
            };
            
            agentCodeMerger.OnUnsuccessfull += () => 
            {
                Debug.LogError("Code merger failed to process solution");
                FinishGenerationActions(); 
            };
        }

        private AbstractAgentHandler NextStepsAfterArchitector(string validationResult,
            AbstractAgentHandler agentCodeMerger, 
            AbstractAgentHandler.ApiKeys apiKeys, 
            string result, 
            AbstractAgentHandler agentSolutionToDicts,
            AgentCodeArchitector agentCodeArchitector,
            string includedCode) 
        {
            AgentCodeMerger agentCodeMergerDirect = agentCodeMerger as AgentCodeMerger;
            var firstRow = validationResult.Split('\n')[0];
            if (firstRow.Contains("1"))
            {
                Debug.Log($"Validation successful. Proceeding with code merging. validationResult: {validationResult}");
                agentCodeMergerDirect.OnComplete += (string mergedCode) => { FinishGenerationActions(); };
                agentCodeMergerDirect.InsertCode(result);
            }
            else
            {
                _currentRun++;
                if (_currentRun > MAX_ALLOWED_VALIDITY_CHECKINGS || !isGeneratingCode)
                {
                    Debug.Log($"Max allowed validity checkings reached. validationResult: {validationResult}");
                    FinishGenerationActions();
                    return agentCodeMerger;
                }
                else
                {
                    Debug.Log($"<color=red>Validation failed.</color> Returning to Architector. validationResult: {validationResult}");
                    if (_currentRun == 2)
                    {
                        UpdateProgress(0.4f);
                        //Debug.Log($"Current run: {_currentRun}; model: {Sanat.ApiAnthropic.Model.Claude35Latest}");
                        Debug.Log($"Current run: {_currentRun}; model: {ApiGeminiModels.Pro}");
                        agentCodeArchitector.ChangeLLM(AbstractAgentHandler.ApiProviders.Gemini, ApiGeminiModels.Pro);
                        //Debug.Log($"Current run: {_currentRun}; model: {Model.GPT4o_16K.Name}");
                        //agentCodeArchitector.ChangeLLM(AbstractAgentHandler.ApiProviders.OpenAI, Model.GPT4o_16K.Name);
                    }else if (_currentRun == 3)
                    {
                        UpdateProgress(0.6f); 
                        //Debug.Log($"Current run: {_currentRun}; model: {Sanat.ApiAnthropic.Model.Claude35Latest}");
                        Debug.Log($"Current run: {_currentRun}; model: {Model.GPT4o1mini.Name}");
                        agentCodeArchitector.ChangeLLM(AbstractAgentHandler.ApiProviders.OpenAI, Model.GPT4o1mini.Name);
                    }else if (_currentRun == 4)
                    {
                        UpdateProgress(0.8f); 
                        Debug.Log($"Current run: {_currentRun}; model: {Model.GPT4o1mini.Name}");
                        agentCodeArchitector.ChangeLLM(AbstractAgentHandler.ApiProviders.OpenAI, Model.GPT4o1mini.Name);
                    }
                    
                    
                    agentCodeArchitector.WorkWithFeedback(validationResult, result, (string feedbackResult) =>
                    {
                        Debug.Log($"Feedback result: {feedbackResult}");
                        AgentCodeValidator agentValidator = new AgentCodeValidator(apiKeys, taskInput, includedCode, feedbackResult);
                        agentValidator.OnComplete += (string validationResult) =>
                        {
                            Debug.Log($"{agentValidator.Name} OnComplete: {validationResult}");
                            NextStepsAfterArchitector(validationResult, agentCodeMerger, 
                                apiKeys, feedbackResult, agentSolutionToDicts, 
                                agentCodeArchitector, includedCode);
                        };
                        agentValidator.Handle(feedbackResult);
                    });
                }
            }

            return agentCodeMerger;
        }

        private void FinishGenerationActions()
        {
            UpdateProgress(1f);
            isGeneratingCode = false;
            _currentRun = 0;
            Repaint();
        }

        private string GeneratePrompt(Dictionary<string, string> projectCode, int rowsToRemove = 0)
        {
            string includedCode = "";
            string includedCodeRaw = "";
            includedCodeRaw = GenerateIncludedCode(projectCode, includedCodeRaw, ref includedCode);
            includedCode = Regex.Replace(includedCode, @"\s+", " ").Trim();
            string clearedCodeScriptForLLM = includedCodeRaw;
            string promptLocation = Application.dataPath + $"{AbstractAgentHandler.PROMPTS_FOLDER_PATH}Unity code architector.md";
            string loadedPrompt = AbstractAgentHandler.LoadPrompt(promptLocation);
            string newPrompt = CodeGeneratorExtensions.RemoveLastNRows(loadedPrompt, rowsToRemove);
            string prompt = newPrompt + $"\n# CODE: {clearedCodeScriptForLLM}\n\n# TASK: {taskInput}";
            generatedPrompt = prompt;
            return clearedCodeScriptForLLM;
        }

        private string GenerateIncludedCode(Dictionary<string, string> projectCode, string includedCodeRaw, ref string includedCode)
        {
            foreach (var kvPathToCode in projectCode)
            {
                foreach (string className in selectedClassNames)
                {
                    if (kvPathToCode.Key.Contains(className))
                    {
                        includedCodeRaw += $"// {kvPathToCode.Key}:\n {kvPathToCode.Value} \n\n";
                        includedCode += File.ReadAllText(kvPathToCode.Key).Replace("\n", "").Replace("\r", "") + "\n\n";
                    }
                }
            }
            
            var includedFolders = IncludedFoldersManager.GetEnabledFolders();
            foreach (var folder in includedFolders)
            {
                string[] files = Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    includedCodeRaw += $"// {file}:\n {File.ReadAllText(file)} \n\n";
                    includedCode += File.ReadAllText(file).Replace("\n", "").Replace("\r", "") + "\n\n";
                }
            }
            
            return includedCodeRaw;
        }

        #region Progress Bar
        private void UpdateProgress(float progress)
        {
            targetProgress = progress;
            if (progress >= 1f)
            {
                isGeneratingCode = false;
                EditorApplication.update -= UpdateProgressBar;
                Repaint();
            }
        }

        public void UpdateProgressBar()
        {
            float currentTime = (float)EditorApplication.timeSinceStartup;
            if (currentTime - lastProgressUpdateTime >= 0.02f)
            {
                if (generationProgress < targetProgress)
                {
                    generationProgress = Mathf.Min(generationProgress + progressSpeed, targetProgress);
                    lastProgressUpdateTime = currentTime;
                    Repaint();
                }
            }
        }

        #endregion

        #region Settings
        public void DrawSettingsFields()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            InsertApiKeyRow("Gemini API Key", "GeminiApiKey", "https://aistudio.google.com/app/apikey", ref geminiApiKey);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            InsertApiKeyRow("OpenAI API Key", "OpenaiApiKey", "https://platform.openai.com/api-keys", ref openaiApiKey);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            InsertApiKeyRow("Groq API Key", "GroqApiKey", "https://console.groq.com/keys", ref groqApiKey);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            InsertApiKeyRow("Antrophic API Key", "AntrophicApiKey", "https://console.anthropic.com/settings/keys", ref antrophicApiKey);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            InsertApiKeyRow("Supabase URL", "SupabaseUrl", "https://supabase.com/dashboard/projects/", ref supabaseUrl);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            InsertApiKeyRow("Supabase Key", "SupabaseKey", "https://supabase.com/dashboard/projects/", ref supabaseKey);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            //is rag field
            EditorGUILayout.LabelField("Is RAG", GUILayout.Width(150));
            isRag = EditorGUILayout.Toggle(isRag);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.BeginHorizontal();
            InsertApiKeyRow("Table Name", "TableName", "https://supabase.com/dashboard/projects/", ref tableName);
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Process All Files for RAG"))
            {
                ProcessAllFilesForRag();
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Ignored Folders:", EditorStyles.boldLabel);
            if (GUILayout.Button("Add Ignored Folder"))
            {
                _ignoredFolders.Add("");
            }
            for (int i = 0; i < _ignoredFolders.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _ignoredFolders[i] = EditorGUILayout.TextField("Folder Path (Ex.: Assets\\Test)", _ignoredFolders[i], GUILayout.ExpandWidth(true));
                if (GUILayout.Button("x", GUILayout.Width(20)))
                {
                    _ignoredFolders.RemoveAt(i);
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Gemini Project Name", GUILayout.Width(150));
            GeminiProjectName = EditorGUILayout.TextField(GeminiProjectName);
            EditorPrefs.SetString(Gemini.PREFS_GEMINI_PROJECT_NAME, GeminiProjectName);
            EditorGUILayout.EndHorizontal();
            
            
            EditorGUILayout.Space();
            IncludedFoldersManager.DrawIncludedFoldersUI();
            EditorGUILayout.EndVertical();
        }
        #endregion

        private void InsertApiKeyRow(string fieldName, string playerPrefsKey, string url, ref string apiKeyVariable)
        {
            EditorGUILayout.LabelField(fieldName, GUILayout.Width(150));
            apiKeyVariable = EditorGUILayout.PasswordField(apiKeyVariable);
            EditorPrefs.SetString(playerPrefsKey, apiKeyVariable);
            if (GUILayout.Button("Get Key", GUILayout.Width(70)))
            {
                Application.OpenURL(url);
            }
        }

        public void RefreshClassList()
        {
            Debug.Log($"{PLUGIN_NAME}Refreshing class list...");
            classToPath.Clear();
            Debug.Log($"{PLUGIN_NAME}Current ignored folders count: {_ignoredFolders.Count}");
            foreach (var ignoredFolder in _ignoredFolders)
            {
                Debug.Log($"{PLUGIN_NAME}Ignored folder path: {ignoredFolder}");
            }

            string[] csFiles = Directory.GetFiles("Assets", "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles("Packages", "*.cs", SearchOption.AllDirectories))
                .Where(filePath => !_ignoredFolders.Any(ignoredFolder => filePath.Contains(ignoredFolder)))
                .ToArray();
            Debug.Log($"{PLUGIN_NAME}Found {csFiles.Length} .cs files.");
            foreach (string filePath in csFiles)
            {
                string className = Path.GetFileNameWithoutExtension(filePath);
                classToPath[className] = filePath;
            }
            Debug.Log($"{PLUGIN_NAME}Class to path dictionary populated with {classToPath.Count} entries.");
        }

        private void SavePromptToFile()
        {
            string directoryPath = Path.Combine(Application.dataPath, PROMPTS_SAVE_FOLDER);
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            string fileName = $"prompt_{DateTime.Now:yyyy-MM-ddTHH-mm-ss}.txt";
            string filePath = Path.Combine(directoryPath, fileName);
            File.WriteAllText(filePath, generatedPrompt);
            Debug.Log($"{PLUGIN_NAME}Prompt of Length = {generatedPrompt.Length} chars   saved to {filePath}");
        }

        private void CopyPromptToClipboard()
        {
            TextEditor te = new TextEditor();
            te.text = generatedPrompt;
            te.SelectAll();
            te.Copy();
        }

        private void StartButtonAnimation()
        {
            isButtonAnimating = true;
            buttonAnimationProgress = 0f;
            buttonColor = Color.yellow;
            EditorApplication.update += UpdateButtonAnimation;
        }

        private void UpdateButtonAnimation()
        {
            buttonAnimationProgress += Time.deltaTime / buttonAnimationTime;
            buttonColor = Color.Lerp(Color.red, Color.green, Mathf.PingPong(buttonAnimationProgress * 2, 1));
            if (buttonAnimationProgress >= 1f)
            {
                isButtonAnimating = false;
                EditorApplication.update -= UpdateButtonAnimation;
            }
        }

        
        public void LoadBookmarkData(CodeGeneratorBookmarks.Bookmark bookmark)
        {
            var bookmarks = bookmarkManager.LoadBookmarksFromPrefs();
            foreach (var bkm in bookmarks)
            {
                if (bkm.Name == bookmark.Name)
                {
                    selectedClassNames = bkm.SelectedClassNames;
                    taskInput = bkm.Task;
                    break;
                }
                
            }
        }

        public void CheckAndHandleFirstLaunch()
        {
            if (!IsFirstLaunch())
            {
                return;
            }

            Debug.Log($"{PLUGIN_NAME}First launch detected. Adding prompt save folder to .gitignore and ignore.conf files.");
            string assetsParentPath = Directory.GetParent(Application.dataPath).FullName;
            string gitignorePath = Path.Combine(assetsParentPath, ".gitignore");
            string ignoreConfPath = Path.Combine(assetsParentPath, "ignore.conf");
            string folderPathToAdd = Environment.NewLine + "/Assets/" + PROMPTS_SAVE_FOLDER + Environment.NewLine;
            string resultsPathToAdd = Environment.NewLine + "/Assets/" + AbstractAgentHandler.RESULTS_SAVE_FOLDER + Environment.NewLine;
            string backupsPathToAdd = Environment.NewLine + "/" + BackupManager.BACKUP_ROOT + "/" + BackupManager.BACKUP_FOLDER_NAME + Environment.NewLine;
            if (File.Exists(gitignorePath))
            {
                if (RequiredExclusionsExist(gitignorePath, folderPathToAdd, resultsPathToAdd, backupsPathToAdd))
                {
                    return;
                }
                File.AppendAllText(gitignorePath, folderPathToAdd);
                File.AppendAllText(gitignorePath, resultsPathToAdd);
            }
            if (File.Exists(ignoreConfPath))
            {
                if (RequiredExclusionsExist(ignoreConfPath, folderPathToAdd, resultsPathToAdd, backupsPathToAdd))
                {
                    return;
                }
                File.AppendAllText(ignoreConfPath, folderPathToAdd);
                File.AppendAllText(ignoreConfPath, resultsPathToAdd);
            }
        }

        private bool RequiredExclusionsExist(string gitignorePath, string folderPathToAdd, string resultsPathToAdd, string backupsPathToAdd)
        {
            string gitignoreContents = File.ReadAllText(gitignorePath);
            if (gitignoreContents.Contains(folderPathToAdd) && gitignoreContents.Contains(resultsPathToAdd) && gitignoreContents.Contains(backupsPathToAdd))
            {
                return true;
            }
            return false;
        }

        private bool IsFirstLaunch()
        {
            const string key = "CODE_GENERATOR_FIRST_LAUNCH";
            bool isFirstLaunch = EditorPrefs.GetInt(key, 1) == 1;
            if (isFirstLaunch)
            {
                EditorPrefs.SetInt(key, 0);
            }
            return isFirstLaunch;
        }
    }
}