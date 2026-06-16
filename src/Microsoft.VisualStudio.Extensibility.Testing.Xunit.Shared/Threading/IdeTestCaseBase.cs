// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for more information.

namespace Xunit.Threading
{
    using System;
    using System.ComponentModel;
    using System.Linq;
    using System.Text;
    using Xunit.Abstractions;
    using Xunit.Harness;
    using Xunit.Sdk;

    public abstract class IdeTestCaseBase : XunitTestCase
    {
        [EditorBrowsable(EditorBrowsableState.Never)]
        [Obsolete("Called by the deserializer; should only be called by deriving classes for deserialization purposes", error: true)]
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        protected IdeTestCaseBase()
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        {
        }

        protected IdeTestCaseBase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod, VisualStudioInstanceKey visualStudioInstanceKey, object?[]? testMethodArguments = null)
            : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, testMethodArguments)
        {
            SharedData = WpfTestSharedData.Instance;
            VisualStudioInstanceKey = visualStudioInstanceKey;

            SkipReason = GetSkipReasonIfNotInstalled(visualStudioInstanceKey.Version);
        }

        public VisualStudioInstanceKey VisualStudioInstanceKey
        {
            get;
            private set;
        }

        public new TestMethodDisplay DefaultMethodDisplay => base.DefaultMethodDisplay;

        public new TestMethodDisplayOptions DefaultMethodDisplayOptions => base.DefaultMethodDisplayOptions;

        public WpfTestSharedData SharedData
        {
            get;
            private set;
        }

        protected virtual bool IncludeRootSuffixInDisplayName => false;

        protected override string GetDisplayName(IAttributeInfo factAttribute, string displayName)
        {
            var baseName = base.GetDisplayName(factAttribute, displayName);
            if (!IncludeRootSuffixInDisplayName || string.IsNullOrEmpty(VisualStudioInstanceKey.RootSuffix))
            {
                return $"{baseName} ({VisualStudioInstanceKey.Version})";
            }
            else
            {
                return $"{baseName} ({VisualStudioInstanceKey.Version}, {VisualStudioInstanceKey.RootSuffix})";
            }
        }

        protected override string GetUniqueID()
        {
            if (string.IsNullOrEmpty(VisualStudioInstanceKey.RootSuffix))
            {
                return $"{base.GetUniqueID()}_{VisualStudioInstanceKey.Version}";
            }
            else
            {
                return $"{base.GetUniqueID()}_{VisualStudioInstanceKey.RootSuffix}_{VisualStudioInstanceKey.Version}";
            }
        }

        public override void Serialize(IXunitSerializationInfo data)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            base.Serialize(data);
            data.AddValue(nameof(VisualStudioInstanceKey), VisualStudioInstanceKey.SerializeToString());
            data.AddValue(nameof(SkipReason), SkipReason);
        }

        public override void Deserialize(IXunitSerializationInfo data)
        {
            if (data is null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            VisualStudioInstanceKey = VisualStudioInstanceKey.DeserializeFromString(data.GetValue<string>(nameof(VisualStudioInstanceKey)));
            base.Deserialize(data);
            SkipReason = data.GetValue<string>(nameof(SkipReason));
            SharedData = WpfTestSharedData.Instance;
        }

        internal static bool IsInstalled(VisualStudioVersion visualStudioVersion)
        {
            return GetSkipReasonIfNotInstalled(visualStudioVersion) is null;
        }

        /// <summary>
        /// Returns <see langword="null"/> if a Visual Studio instance with the major version corresponding to
        /// <paramref name="visualStudioVersion"/> was discovered via the Visual Studio Setup Configuration COM API.
        /// Otherwise returns a multi-line skip reason describing what was detected and how to address the problem.
        /// </summary>
        internal static string? GetSkipReasonIfNotInstalled(VisualStudioVersion visualStudioVersion)
        {
            int expectedMajorVersion;

            switch (visualStudioVersion)
            {
            case VisualStudioVersion.VS2012:
                expectedMajorVersion = 11;
                break;

            case VisualStudioVersion.VS2013:
                expectedMajorVersion = 12;
                break;

            case VisualStudioVersion.VS2015:
                expectedMajorVersion = 14;
                break;

            case VisualStudioVersion.VS2017:
                expectedMajorVersion = 15;
                break;

            case VisualStudioVersion.VS2019:
                expectedMajorVersion = 16;
                break;

            case VisualStudioVersion.VS2022:
                expectedMajorVersion = 17;
                break;

            case VisualStudioVersion.VS18:
                expectedMajorVersion = 18;
                break;

            default:
                throw new ArgumentException();
            }

            var sb = new StringBuilder();

            try
            {
                var detected = VisualStudioInstanceFactory.EnumerateVisualStudioInstances().ToList();
                if (detected.Any(i => i.Item2.Major == expectedMajorVersion))
                {
                    return null;
                }

                sb.Append(visualStudioVersion).Append(" is not installed: no Visual Studio instance with major version ").Append(expectedMajorVersion).AppendLine(" was found by the Visual Studio Setup Configuration COM API.");
                sb.AppendLine();
                if (detected.Count == 0)
                {
                    sb.AppendLine("No Visual Studio instances were detected.");
                }
                else
                {
                    sb.Append("Detected Visual Studio instance(s) (").Append(detected.Count).AppendLine("):");
                    foreach (var instance in detected)
                    {
                        sb.Append("  - ").Append(instance.Item2).Append(" (").Append(instance.Item4).Append(") at ").AppendLine(instance.Item1);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.Append(visualStudioVersion).Append(" could not be detected: failed to enumerate Visual Studio instances (").Append(ex.GetType().Name).Append(": ").Append(ex.Message).AppendLine(").");
                sb.AppendLine();
                sb.AppendLine("The Visual Studio Setup Configuration COM service is required to discover installed instances. Re-running the Visual Studio Installer typically restores it.");
            }

            sb.AppendLine();
            sb.AppendLine("To run these tests:");
            sb.Append("  - Install a Visual Studio release with major version ").Append(expectedMajorVersion).Append(" (").Append(visualStudioVersion).AppendLine(") via the Visual Studio Installer.");
            sb.AppendLine("  - Or set the VSInstallDir environment variable to the installation path of an installed VS instance with the required major version, then re-run the tests.");
            sb.AppendLine("  - Verify what the Setup Configuration COM API reports by running:");
            sb.AppendLine("      \"%ProgramFiles(x86)%\\Microsoft Visual Studio\\Installer\\vswhere.exe\" -all -prerelease -format json");

            return sb.ToString().TrimEnd();
        }
    }
}
