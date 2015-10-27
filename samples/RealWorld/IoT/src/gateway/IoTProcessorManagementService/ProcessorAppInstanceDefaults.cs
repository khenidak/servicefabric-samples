﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IoTProcessorManagementService
{
    public class ProcessorManagementServiceConfig
    {
        public ProcessorManagementServiceConfig(string processorAppTypeName,
                                   string processorAppTypeVersion,
                                   string processorServiceTypeName,
                                   string processorAppInstanceNamePrefix)
        {
            AppTypeName = processorAppTypeName;
            AppTypeVersion = processorAppTypeVersion;
            ServiceTypeName = processorServiceTypeName;
            AppInstanceNamePrefix = processorAppInstanceNamePrefix;
        }
        public readonly string AppTypeName;
        public readonly string AppTypeVersion;
        public readonly string ServiceTypeName;
        public readonly string AppInstanceNamePrefix;
    }
}