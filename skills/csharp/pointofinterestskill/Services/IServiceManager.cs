// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using SkillServiceLibrary.Services;

namespace PointOfInterestSkill.Services
{
    public interface IServiceManager
    {
        IGeoSpatialService InitMapsService(BotSettings services, string locale = "en-us");

        IGeoSpatialService InitAddressMapsService(BotSettings services, string locale = "en-us");

        IGeoSpatialService InitRoutingMapsService(BotSettings services, string locale = "en-us");
    }
}