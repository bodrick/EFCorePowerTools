﻿using EFCorePowerTools.Contracts.Views;
using EFCorePowerTools.Extensions;
using EFCorePowerTools.Handlers.ReverseEngineer;
using EFCorePowerTools.Helpers;
using EFCorePowerTools.Shared.Models;
using EnvDTE;
using ErikEJ.SqlCeToolbox.Helpers;
using ReverseEngineer20;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using ReverseEngineer20.ReverseEngineer;

namespace EFCorePowerTools.Handlers
{
    internal class ReverseEngineerHandler
    {
        private readonly EFCorePowerToolsPackage _package;
        private readonly ReverseEngineerHelper reverseEngineerHelper;

        public ReverseEngineerHandler(EFCorePowerToolsPackage package)
        {
            _package = package;
            reverseEngineerHelper = new ReverseEngineerHelper();
        }

        public async void ReverseEngineerCodeFirst(Project project)
        {
            try
            {
                var dteH = new EnvDteHelper();
                string dacpacSchema = null;

                if (_package.Dte2.Mode == vsIDEMode.vsIDEModeDebug)
                {
                    EnvDteHelper.ShowError("Cannot generate code while debugging");
                    return;
                }

                var projectPath = project.Properties.Item("FullPath")?.Value.ToString();
                var optionsPaths = project.GetConfigFiles();
                var optionsPath = optionsPaths.FirstOrDefault();
                var renamingPath = project.GetRenamingPath();

                if (optionsPaths.Count > 1)
                {
                    var pcd = _package.GetView<IPickConfigDialog>();
                    pcd.PublishConfigurations(optionsPaths.Select(m => new ConfigModel
                    {
                        ConfigPath = m,
                    }));

                    var pickConfigResult = pcd.ShowAndAwaitUserResponse(true);
                    if (!pickConfigResult.ClosedByOK)
                        return;

                    optionsPath = pickConfigResult.Payload.ConfigPath;
                }

                var databaseList = EnvDteHelper.GetDataConnections(_package);
                var dacpacList = _package.Dte2.DTE.GetDacpacFilesInActiveSolution(EnvDteHelper.GetProjectFilesInSolution(_package));
                var options = ReverseEngineerOptionsExtensions.TryRead(optionsPath);

                var psd = _package.GetView<IPickServerDatabaseDialog>();

                if (databaseList != null && databaseList.Any())
                {
                    psd.PublishConnections(databaseList.Select(m => new DatabaseConnectionModel
                    {
                        ConnectionName = m.Value.Caption,
                        ConnectionString = m.Value.ConnectionString,
                        DatabaseType = m.Value.DatabaseType
                    }));
                }

                if (dacpacList != null && dacpacList.Any())
                {
                    psd.PublishDefinitions(dacpacList.Select(m => new DatabaseDefinitionModel
                    {
                        FilePath = m
                    }));
                }

                var pickDataSourceResult = psd.ShowAndAwaitUserResponse(true);
                if (!pickDataSourceResult.ClosedByOK)
                    return;

                var useEFCore5 = pickDataSourceResult.Payload.IncludeViews;
                var filterSchemas = pickDataSourceResult.Payload.FilterSchemas;
                var schemas = filterSchemas ? pickDataSourceResult.Payload.Schemas : null;

                _package.Dte2.StatusBar.Text = "Loading table list...";

                // Reload the database list, in case the user has added a new database in the dialog
                databaseList = EnvDteHelper.GetDataConnections(_package);

                DatabaseInfo dbInfo = null;
                if (pickDataSourceResult.Payload.Connection != null)
                {
                    dbInfo = databaseList.Single(m => m.Value.ConnectionString == pickDataSourceResult.Payload.Connection?.ConnectionString).Value;
                }
                var dacpacPath = pickDataSourceResult.Payload.Definition?.FilePath;

                if (dbInfo == null) dbInfo = new DatabaseInfo();

                if (!string.IsNullOrEmpty(dacpacPath))
                {
                    dbInfo.DatabaseType = DatabaseType.SQLServer;
                    dbInfo.ConnectionString = "Data Source=.;Initial Catalog=" + Path.GetFileNameWithoutExtension(dacpacPath);
                    dacpacPath = _package.Dte2.DTE.BuildSqlProj(dacpacPath);
                    if (string.IsNullOrEmpty(dacpacPath))
                    {
                        EnvDteHelper.ShowMessage("Unable to build selected Database Project");
                        return;
                    }
                }

                if (dbInfo.DatabaseType == DatabaseType.SQLCE35)
                {
                    EnvDteHelper.ShowError($"Unsupported provider: {dbInfo.ServerVersion}");
                    return;
                }

                if (dbInfo.DatabaseType == DatabaseType.SQLCE40)
                {
                    EnvDteHelper.ShowError($"Unsupported provider with EF Core 3.0: {dbInfo.DatabaseType}");
                    return;
                }

                List<TableInformationModel> predefinedTables = !string.IsNullOrEmpty(dacpacPath)
                                           ? GetDacpacTables(dacpacPath, useEFCore5)
                                           : GetTables(dbInfo.ConnectionString, dbInfo.DatabaseType, useEFCore5, schemas);

                var preselectedTables = new List<TableInformationModel>();
                if (options != null)
                {
                    dacpacSchema = options.DefaultDacpacSchema;
                    if (options.Tables.Count > 0)
                    {
                        var normalizedTables = reverseEngineerHelper.NormalizeTables(options.Tables, dbInfo.DatabaseType == DatabaseType.SQLServer);
                        preselectedTables.AddRange(normalizedTables);
                    }
                }

                var ptd = _package.GetView<IPickTablesDialog>()
                                  .AddTables(predefinedTables)
                                  .PreselectTables(preselectedTables);
                
                var customNameOptions = CustomNameOptionsExtensions.TryRead(renamingPath);

                var pickTablesResult = ptd.ShowAndAwaitUserResponse(true);
                if (!pickTablesResult.ClosedByOK) return;

                var classBasis = EnvDteHelper.GetDatabaseName(dbInfo.ConnectionString, dbInfo.DatabaseType);
                var model = reverseEngineerHelper.GenerateClassName(classBasis) + "Context";
                var packageResult = project.ContainsEfCoreReference(dbInfo.DatabaseType);

                var presets = new ModelingOptionsModel
                {
                    InstallNuGetPackage = !packageResult.Item1,
                    ModelName = options != null ? options.ContextClassName : model,
                    ProjectName = project.Name,
                    Namespace = options != null ? options.ProjectRootNamespace : project.Properties.Item("DefaultNamespace").Value.ToString(),
                    DacpacPath = dacpacPath,
                };
                if (options != null)
                {
                    presets.UseDataAnnotations = !options.UseFluentApiOnly;
                    presets.UseDatabaseNames = options.UseDatabaseNames;
                    presets.UsePluralizer = options.UseInflector;
                    presets.UseDbContextSplitting = options.UseDbContextSplitting;
                    presets.UseHandelbars = options.UseHandleBars;
                    presets.SelectedHandlebarsLanguage = options.SelectedHandlebarsLanguage;
                    presets.IncludeConnectionString = options.IncludeConnectionString;
                    presets.ModelName = options.ContextClassName;
                    presets.Namespace = options.ProjectRootNamespace;
                    presets.OutputPath = options.OutputPath;
                    presets.OutputContextPath = options.OutputContextPath;
                    presets.ModelNamespace = options.ModelNamespace;
                    presets.ContextNamespace = options.ContextNamespace;
                    presets.SelectedToBeGenerated = options.SelectedToBeGenerated;
                    presets.DacpacPath = options.Dacpac;
                    presets.UseStoredProcedures = options.UseStoredProcedures;
                    presets.UseEf6Pluralizer = options.UseLegacyPluralizer;
                    presets.MapSpatialTypes = options.UseSpatial;
                    presets.MapNodaTimeTypes = options.UseNodaTime;
                }

                var modelDialog = _package.GetView<IModelingOptionsDialog>()
                                          .ApplyPresets(presets);

                _package.Dte2.StatusBar.Text = "Getting options...";

                var modelingOptionsResult = modelDialog.ShowAndAwaitUserResponse(true);
                if (!modelingOptionsResult.ClosedByOK) return;

                options = new ReverseEngineerOptions
                {
                    UseFluentApiOnly = !modelingOptionsResult.Payload.UseDataAnnotations,
                    ConnectionString = dbInfo.ConnectionString,
                    ContextClassName = modelingOptionsResult.Payload.ModelName,
                    DatabaseType = (ReverseEngineer20.DatabaseType)dbInfo.DatabaseType,
                    ProjectPath = projectPath,
                    OutputPath = modelingOptionsResult.Payload.OutputPath,
                    OutputContextPath = modelingOptionsResult.Payload.OutputContextPath,
                    ContextNamespace = modelingOptionsResult.Payload.ContextNamespace,
                    ModelNamespace = modelingOptionsResult.Payload.ModelNamespace,
                    ProjectRootNamespace = modelingOptionsResult.Payload.Namespace,
                    UseDatabaseNames = modelingOptionsResult.Payload.UseDatabaseNames,
                    UseInflector = modelingOptionsResult.Payload.UsePluralizer,
                    UseLegacyPluralizer = modelingOptionsResult.Payload.UseEf6Pluralizer,
                    UseSpatial = modelingOptionsResult.Payload.MapSpatialTypes,
                    UseNodaTime = modelingOptionsResult.Payload.MapNodaTimeTypes,
                    UseStoredProcedures = modelingOptionsResult.Payload.UseStoredProcedures,
                    UseDbContextSplitting = modelingOptionsResult.Payload.UseDbContextSplitting,
                    UseHandleBars = modelingOptionsResult.Payload.UseHandelbars,
                    SelectedHandlebarsLanguage = modelingOptionsResult.Payload.SelectedHandlebarsLanguage,
                    IncludeConnectionString = modelingOptionsResult.Payload.IncludeConnectionString,
                    SelectedToBeGenerated = modelingOptionsResult.Payload.SelectedToBeGenerated,
                    Dacpac = dacpacPath,
                    DefaultDacpacSchema = dacpacSchema,
                    Tables = pickTablesResult.Payload.ToList(),
                    CustomReplacers = customNameOptions,
                    FilterSchemas = filterSchemas,
                    Schemas = schemas?.ToList()
                };

                if (options.DatabaseType == DatabaseType.SQLServer
                    && string.IsNullOrEmpty(options.Dacpac))
                {
                    var hasRights = reverseEngineerHelper.HasSqlServerViewDefinitionRights(options.ConnectionString);

                    if (hasRights == false)
                    {
                        EnvDteHelper.ShowMessage("The SQL Server user does not have 'VIEW DEFINITION' rights, default constraints may not be available.");        
                    }
                }

                var tfm = project.Properties.Item("TargetFrameworkMoniker").Value.ToString();
                bool isNetStandard = tfm.Contains(".NETStandard,Version=v2.");

                if (modelingOptionsResult.Payload.UseHandelbars)
                {
                    var dropped = (DropTemplates(projectPath));
                    if (dropped && !project.IsNetCore() && !isNetStandard)
                    {
                        project.ProjectItems.AddFromDirectory(Path.Combine(projectPath, "CodeTemplates"));
                    }
                }

                var startTime = DateTime.Now;

                _package.Dte2.StatusBar.Text = "Generating code...";

                var revEngResult = EfRevEngLauncher.LaunchExternalRunner(options, useEFCore5);

                if (modelingOptionsResult.Payload.SelectedToBeGenerated == 0 || modelingOptionsResult.Payload.SelectedToBeGenerated == 2)
                {
                    foreach (var filePath in revEngResult.EntityTypeFilePaths)
                    {
                        if (!project.IsNetCore() && !isNetStandard)
                        {
                            project.ProjectItems.AddFromFile(filePath);
                        }
                    }
                    if (modelingOptionsResult.Payload.SelectedToBeGenerated == 2)
                    {
                        if (File.Exists(revEngResult.ContextFilePath)) File.Delete(revEngResult.ContextFilePath);
                        foreach (var filePath in revEngResult.ContextConfigurationFilePaths)
                        {
                            if (File.Exists(filePath)) File.Delete(filePath);
                        }
                    }
                }

                if (modelingOptionsResult.Payload.SelectedToBeGenerated == 0 || modelingOptionsResult.Payload.SelectedToBeGenerated == 1)
                {
                    if (!project.IsNetCore() && !isNetStandard)
                    {
                        foreach (var filePath in revEngResult.ContextConfigurationFilePaths)
                        {
                            project.ProjectItems.AddFromFile(filePath);
                        }
                        project.ProjectItems.AddFromFile(revEngResult.ContextFilePath);
                    }
                    
                    _package.Dte2.ItemOperations.OpenFile(revEngResult.ContextFilePath);

                    if (modelingOptionsResult.Payload.SelectedToBeGenerated == 1)
                    {
                        foreach (var filePath in revEngResult.EntityTypeFilePaths)
                        {
                            if (File.Exists(filePath)) File.Delete(filePath);
                        }
                    }
                }

                var missingProviderPackage = packageResult.Item1 ? null : packageResult.Item2;
                if (modelingOptionsResult.Payload.InstallNuGetPackage || modelingOptionsResult.Payload.SelectedToBeGenerated == 2)
                {
                    missingProviderPackage = null;
                }

                _package.Dte2.StatusBar.Text = "Reporting result...";
                var errors = reverseEngineerHelper.ReportRevEngErrors(revEngResult, missingProviderPackage);

                SaveOptions(project, optionsPath, options);

                if (modelingOptionsResult.Payload.InstallNuGetPackage)
                {
                    _package.Dte2.StatusBar.Text = "Installing EF Core provider package";
                    var nuGetHelper = new NuGetHelper();
                    await nuGetHelper.InstallPackageAsync(packageResult.Item2, project);
                }
                var duration = DateTime.Now - startTime;
                _package.Dte2.StatusBar.Text = $"Reverse engineer completed in {duration:h\\:mm\\:ss}";

                EnvDteHelper.ShowMessage(errors);

                if (revEngResult.EntityErrors.Count > 0)
                {
                    _package.LogError(revEngResult.EntityErrors, null);
                }
                if (revEngResult.EntityWarnings.Count > 0)
                {
                    _package.LogError(revEngResult.EntityWarnings, null);
                }
                Telemetry.TrackEvent("PowerTools.ReverseEngineer");
            }
            catch (AggregateException ae)
            {
                foreach (var innerException in ae.Flatten().InnerExceptions)
                {
                    _package.LogError(new List<string>(), innerException);
                }
            }
            catch (Exception exception)
            {
                _package.LogError(new List<string>(), exception);
            }
        }

        private void SaveOptions(Project project, string optionsPath, ReverseEngineerOptions options)
        {
            if (!File.Exists(optionsPath + ".ignore"))
            {
                File.WriteAllText(optionsPath, options.Write(), Encoding.UTF8);
                project.ProjectItems.AddFromFile(optionsPath);
            }
        }

        private bool DropTemplates(string projectPath)
        {
            var toDir = Path.Combine(projectPath, "CodeTemplates");
            var fromDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!Directory.Exists(toDir))
            {
                Directory.CreateDirectory(toDir);
                ZipFile.ExtractToDirectory(Path.Combine(fromDir, "CodeTemplates.zip"), toDir);
                return true;
            }

            return false;
        }

        public List<TableInformationModel> GetDacpacTables(string dacpacPath, bool useEFCore5)
        {
            var builder = new TableListBuilder(dacpacPath, DatabaseType.Undefined, null);
            return builder.GetTableDefinitions(useEFCore5);
        }

        public List<TableInformationModel> GetTables(string connectionString, DatabaseType databaseType, bool useEFCore5, SchemaInfo[] schemas)
        {
            var builder = new TableListBuilder(connectionString, databaseType, schemas);
            return builder.GetTableDefinitions(useEFCore5);
        }
    }
}