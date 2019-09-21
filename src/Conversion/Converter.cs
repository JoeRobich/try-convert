﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Facts;
using Microsoft.Build.Construction;
using MSBuildAbstractions;
using PackageConversion;

namespace Conversion
{
    public class Converter
    {
        private readonly UnconfiguredProject _project;
        private readonly BaselineProject _sdkBaselineProject;
        private readonly IProjectRootElement _projectRootElement;
        private readonly ImmutableDictionary<string, Differ> _differs;

        public Converter(UnconfiguredProject project, BaselineProject sdkBaselineProject, IProjectRootElement projectRootElement)
        {
            _project = project ?? throw new ArgumentNullException(nameof(project));
            _sdkBaselineProject = sdkBaselineProject;
            _projectRootElement = projectRootElement ?? throw new ArgumentNullException(nameof(projectRootElement));
            _differs = GetDiffers();
        }

        public void Convert(string outputPath)
        {
            GenerateProjectFile();
            _projectRootElement.Save(outputPath);
        }

        internal IProjectRootElement GenerateProjectFile()
        {
            ChangeImports();

            RemoveDefaultedProperties();
            RemoveUnnecessaryPropertiesNotInSDKByDefault();

            var tfm = AddTargetFrameworkProperty();
            AddGenerateAssemblyInfo();
            AddDesktopProperties();
            AddCommonPropertiesToTopLevelPropertyGroup();

            AddConvertedPackages(tfm);
            RemoveOrUpdateItems(tfm);

            // Does not appear to be necessary.
            // This also clutters up the project file whenever you have embedded resources like images.
            //AddItemRemovesForIntroducedItems();

            ModifyProjectElement();

            return _projectRootElement;
        }

        internal ImmutableDictionary<string, Differ> GetDiffers() =>
            _project.ConfiguredProjects.Select(p => (p.Key, new Differ(p.Value, _sdkBaselineProject.Project.ConfiguredProjects[p.Key]))).ToImmutableDictionary(kvp => kvp.Key, kvp => kvp.Item2);

        private void ChangeImports()
        {
            var projectStyle = _sdkBaselineProject.ProjectStyle;

            if (projectStyle == ProjectStyle.Default || projectStyle == ProjectStyle.WindowsDesktop)
            {
                foreach (var import in _projectRootElement.Imports)
                {
                    _projectRootElement.RemoveChild(import);
                }

                if (MSBuildHelpers.IsWinForms(_projectRootElement) || MSBuildHelpers.IsWPF(_projectRootElement))
                {
                    _projectRootElement.Sdk = DesktopFacts.WinSDKAttribute;
                }
                else
                {
                    _projectRootElement.Sdk = MSBuildFacts.DefaultSDKAttribute;
                }
            }
        }

        private void RemoveDefaultedProperties()
        {
            foreach (var propGroup in _projectRootElement.PropertyGroups)
            {
                var configurationName = MSBuildHelpers.GetConfigurationName(propGroup.Condition);
                var propDiff = _differs[configurationName].GetPropertiesDiff();

                foreach (var prop in propGroup.Properties)
                {
                    // These properties were added to the baseline - so don't treat them as defaulted properties.
                    if (_sdkBaselineProject.GlobalProperties.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (propDiff.DefaultedProperties.Select(p => p.Name).Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        propGroup.RemoveChild(prop);
                    }
                }

                if (propGroup.Properties.Count == 0)
                {
                    _projectRootElement.RemoveChild(propGroup);
                }
            }
        }

        private void RemoveUnnecessaryPropertiesNotInSDKByDefault()
        {
            static string GetProjectName(string projectPath)
            {
                var projName = projectPath.Split('\\').Last();
                return projName.Substring(0, projName.LastIndexOf('.'));
            }

            foreach (var propGroup in _projectRootElement.PropertyGroups)
            {
                foreach (var prop in propGroup.Properties)
                {
                    if (MSBuildFacts.UnnecessaryProperties.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        propGroup.RemoveChild(prop);
                    }
                    else if (ProjectPropertyHelpers.IsDefineConstantDefault(prop))
                    {
                        propGroup.RemoveChild(prop);
                    }
                    else if (ProjectPropertyHelpers.IsDebugTypeDefault(prop))
                    {
                        propGroup.RemoveChild(prop);
                    }
                    else if (ProjectPropertyHelpers.IsOutputPathDefault(prop))
                    {
                        propGroup.RemoveChild(prop);
                    }
                    else if (ProjectPropertyHelpers.IsPlatformTargetDefault(prop))
                    {
                        propGroup.RemoveChild(prop);
                    }
                    else if (ProjectPropertyHelpers.IsNameDefault(prop, GetProjectName(_projectRootElement.FullPath)))
                    {
                        propGroup.RemoveChild(prop);
                    }
                    else if (ProjectPropertyHelpers.IsDocumentationFileDefault(prop))
                    {
                        propGroup.RemoveChild(prop);
                    }
                }

                if (propGroup.Properties.Count == 0)
                {
                    _projectRootElement.RemoveChild(propGroup);
                }
            }
        }

        private void RemoveOrUpdateItems(string tfm)
        {
            static void UpdateBasedOnDiff(ImmutableArray<ItemsDiff> itemsDiff, ProjectItemGroupElement itemGroup, ProjectItemElement item)
            {
                var itemTypeDiff = itemsDiff.FirstOrDefault(id => id.ItemType.Equals(item.ItemType, StringComparison.OrdinalIgnoreCase));
                if (!itemTypeDiff.DefaultedItems.IsDefault)
                {
                    var defaultedItems = itemTypeDiff.DefaultedItems.Select(i => i.EvaluatedInclude);
                    if (defaultedItems.Contains(item.Include, StringComparer.OrdinalIgnoreCase))
                    {
                        itemGroup.RemoveChild(item);
                    }
                }

                if (!itemTypeDiff.ChangedItems.IsDefault)
                {
                    var changedItems = itemTypeDiff.ChangedItems.Select(i => i.EvaluatedInclude);
                    if (changedItems.Contains(item.Include, StringComparer.OrdinalIgnoreCase))
                    {
                        var path = item.Include;
                        item.Include = null;
                        item.Update = path;
                    }
                }
            }

            static bool IsDesktopRemovableItem(BaselineProject sdkBaselineProject, ProjectItemGroupElement itemGroup, ProjectItemElement item)
            {
                return sdkBaselineProject.ProjectStyle == ProjectStyle.WindowsDesktop
                       && (ProjectItemHelpers.IsLegacyXamlDesignerItem(item)
                           || ProjectItemHelpers.IsDependentUponXamlDesignerItem(item)
                           || ProjectItemHelpers.IsDesignerFile(item)
                           || ProjectItemHelpers.IsSettingsFile(item)
                           || ProjectItemHelpers.IsResxFile(item)
                           || ProjectItemHelpers.DesktopReferencesNeedsRemoval(item)
                           || ProjectItemHelpers.IsDesktopRemovableGlobbedItem(sdkBaselineProject.ProjectStyle, item));
            }

            foreach (var itemGroup in _projectRootElement.ItemGroups)
            {
                var configurationName = MSBuildHelpers.GetConfigurationName(itemGroup.Condition);

                foreach (var item in itemGroup.Items.Where(item => !ProjectItemHelpers.IsPackageReference(item)))
                {
                    if (item.HasMetadata && ProjectItemHelpers.CanItemMetadataBeRemoved(item))
                    {
                        foreach (var metadataElement in item.Metadata)
                        {
                            item.RemoveChild(metadataElement);
                        }
                    }

                    if (MSBuildFacts.UnnecessaryItemIncludes.Contains(item.Include, StringComparer.OrdinalIgnoreCase))
                    {
                        itemGroup.RemoveChild(item);
                    }
                    else if (ProjectItemHelpers.IsExplicitValueTupleReferenceThatCanBeRemoved(item, tfm))
                    {
                        itemGroup.RemoveChild(item);
                    }
                    else if (ProjectItemHelpers.IsReferenceConvertibleToPackageReference(item))
                    {
                        var packageName = item.Include;
                        var version = MSBuildFacts.DefaultItemsThatHavePackageEquivalents[packageName];

                        AddPackage(packageName, version);

                        itemGroup.RemoveChild(item);
                    }
                    else if (IsDesktopRemovableItem(_sdkBaselineProject, itemGroup, item))
                    {
                        itemGroup.RemoveChild(item);
                    }
                    else if (ProjectItemHelpers.IsItemWithUnnecessaryMetadata(item))
                    {
                        itemGroup.RemoveChild(item);
                    }
                    else
                    {
                        var itemsDiff = _differs[configurationName].GetItemsDiff();
                        UpdateBasedOnDiff(itemsDiff, itemGroup, item);
                    }
                }

                if (itemGroup.Items.Count == 0)
                {
                    _projectRootElement.RemoveChild(itemGroup);
                }
            }
        }

        private void AddPackage(string packageName, string packageVersion)
        {
            var groupForPackageRefs = MSBuildHelpers.GetOrCreatePackageReferencesItemGroup(_projectRootElement);

            var metadata = new List<KeyValuePair<string, string>>()
            {
                new KeyValuePair<string, string>("Version", packageVersion)
            };

            groupForPackageRefs.AddItem(PackageFacts.PackageReferenceItemType, packageName, metadata);
        }

        private void AddConvertedPackages(string tfm)
        {
            var packagesConfigItemGroup = MSBuildHelpers.GetPackagesConfigItemGroup(_projectRootElement);
            if (packagesConfigItemGroup is null)
            {
                return;
            }

            var packagesConfigItem = MSBuildHelpers.GetPackagesConfigItem(packagesConfigItemGroup);
            var path = Path.Combine(_projectRootElement.DirectoryPath, packagesConfigItem.Include);
            
            var packageReferences = PackagesConfigConverter.Convert(path);
            if (packageReferences is object && packageReferences.Any())
            {
                var groupForPackageRefs = _projectRootElement.AddItemGroup();
                foreach (var pkgref in packageReferences)
                {
                    if (pkgref.ID.Equals(MSBuildFacts.SystemValueTupleName, StringComparison.OrdinalIgnoreCase) && MSBuildHelpers.FrameworkHasAValueTuple(tfm))
                    {
                        continue;
                    }

                    if (MSBuildFacts.UnnecessaryItemIncludes.Contains(pkgref.ID, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    // TODO: more metadata?
                    var metadata = new List<KeyValuePair<string, string>>()
                    {
                        new KeyValuePair<string, string>("Version", pkgref.Version)
                    };

                    // TODO: some way to make Version not explicitly metadata
                    var item = groupForPackageRefs.AddItem(PackageFacts.PackageReferenceItemType, pkgref.ID, metadata);
                }

                // If the only references we had are already in the SDK, we're done.
                if (!groupForPackageRefs.Items.Any())
                {
                    _projectRootElement.RemoveChild(groupForPackageRefs);
                }
            }

            packagesConfigItemGroup.RemoveChild(packagesConfigItem);
        }

        private string AddTargetFrameworkProperty()
        {
            static string StripDecimals(string tfm)
            {
                var parts = tfm.Split('.');
                return string.Join("", parts);
            }

            if (_sdkBaselineProject.GlobalProperties.Contains("TargetFramework", StringComparer.OrdinalIgnoreCase))
            {
                // The original project had a TargetFramework property. No need to add it again.
                return _sdkBaselineProject.GlobalProperties.First(p => p.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase));
            }

            var propGroup = MSBuildHelpers.GetOrCreateEmptyPropertyGroup(_sdkBaselineProject, _projectRootElement);

            var targetFrameworkElement = _projectRootElement.CreatePropertyElement("TargetFramework");

            if (_sdkBaselineProject.ProjectStyle == ProjectStyle.WindowsDesktop)
            {
                targetFrameworkElement.Value = Facts.MSBuildFacts.NETCoreDesktopTFM;
            }
            else
            {
                var rawTFM = _sdkBaselineProject.Project.FirstConfiguredProject.GetProperty("TargetFramework").EvaluatedValue;

                // This is pretty much never gonna happen, but it was cheap to write the code
                targetFrameworkElement.Value = MSBuildHelpers.IsNotNetFramework(rawTFM) ? StripDecimals(rawTFM) : rawTFM;
            }

            propGroup.PrependChild(targetFrameworkElement);

            return targetFrameworkElement.Value;
        }

        private void AddDesktopProperties()
        {
            if (_sdkBaselineProject.ProjectStyle != ProjectStyle.WindowsDesktop)
            {
                return;
            }

            // Don't create a new prop group; put the desktop properties in the same group as where TFM is located
            var propGroup = MSBuildHelpers.GetOrCreateTopLevelPropertyGroupWithTFM(_projectRootElement);

            if (!_sdkBaselineProject.GlobalProperties.Contains(DesktopFacts.UseWinFormsPropertyName, StringComparer.OrdinalIgnoreCase) && MSBuildHelpers.IsWinForms(_projectRootElement))
            {
                var useWinForms = _projectRootElement.CreatePropertyElement(DesktopFacts.UseWinFormsPropertyName);
                useWinForms.Value = "true";
                propGroup.AppendChild(useWinForms);
            }

            if (!_sdkBaselineProject.GlobalProperties.Contains(DesktopFacts.UseWPFPropertyName, StringComparer.OrdinalIgnoreCase) && MSBuildHelpers.IsWPF(_projectRootElement))
            {
                var useWPF = _projectRootElement.CreatePropertyElement(DesktopFacts.UseWPFPropertyName);
                useWPF.Value = "true";
                propGroup.AppendChild(useWPF);
            }
        }

        private void AddCommonPropertiesToTopLevelPropertyGroup()
        {
            var propGroups = _projectRootElement.PropertyGroups;

            // If there is only 1, it's the top-level group.
            // If there are only 2, then the remaining group has unqiue properties in it that may be configuration-specific.
            if (propGroups.Count <= 2)
            {
                return;
            }

            var pairs = propGroups.Zip(propGroups.Skip(1), (pgA, pgB) => (pgA, pgB))
                                  .Where(pair => MSBuildHelpers.ArePropertyGroupElementsIdentical(pair.pgA, pair.pgB));

            var topLevelPropGroup = MSBuildHelpers.GetOrCreateTopLevelPropertyGroupWithTFM(_projectRootElement);

            foreach (var (a,b) in pairs)
            {
                foreach (var prop in a.Properties)
                {
                    if (prop.Parent is object)
                    {
                        a.RemoveChild(prop);
                    }

                    if (!topLevelPropGroup.Properties.Any(p => ProjectPropertyHelpers.ArePropertiesEqual(p, prop)))
                    {
                        topLevelPropGroup.AppendChild(prop);
                    }
                }

                foreach (var prop in b.Properties)
                {
                    if (prop.Parent is object)
                    {
                        b.RemoveChild(prop);
                    }
                }

                if (a.Parent is object)
                {
                    _projectRootElement.RemoveChild(a);
                }

                if (b.Parent is object)
                {
                    _projectRootElement.RemoveChild(b);
                }
            }
        }

        private void AddGenerateAssemblyInfo()
        {
            // Don't create a new prop group; put the desktop properties in the same group as where TFM is located
            var propGroup = MSBuildHelpers.GetOrCreateTopLevelPropertyGroupWithTFM(_projectRootElement);
            var generateAssemblyInfo = _projectRootElement.CreatePropertyElement(MSBuildFacts.GenerateAssemblyInfoNodeName);
            generateAssemblyInfo.Value = "false";
            propGroup.AppendChild(generateAssemblyInfo);
        }

        private void ModifyProjectElement()
        {
            _projectRootElement.ToolsVersion = null;
            _projectRootElement.DefaultTargets = null;
        }
    }
}
