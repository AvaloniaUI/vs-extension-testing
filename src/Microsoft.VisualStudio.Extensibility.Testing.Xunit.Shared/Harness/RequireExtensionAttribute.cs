// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for more information.

namespace Xunit.Harness
{
    using System;

    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public class RequireExtensionAttribute : Attribute
    {
        public RequireExtensionAttribute(string extensionFile)
            : this(extensionFile, VisualStudioVersion.Unspecified)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="RequireExtensionAttribute"/> class that
        /// requires <paramref name="extensionFile"/> only in instances started for
        /// <paramref name="version"/>.
        /// </summary>
        public RequireExtensionAttribute(string extensionFile, VisualStudioVersion version)
        {
            ExtensionFile = extensionFile;
            Version = version;
        }

        public string ExtensionFile
        {
            get;
        }

        /// <summary>
        /// Gets the version the extension is limited to.
        /// <see cref="VisualStudioVersion.Unspecified"/> means every version.
        /// </summary>
        public VisualStudioVersion Version
        {
            get;
        }
    }
}
