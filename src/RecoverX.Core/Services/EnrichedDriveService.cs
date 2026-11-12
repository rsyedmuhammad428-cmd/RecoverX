using System;
using System.Collections.Generic;
using RecoverX.Core.Interfaces;
using RecoverX.Core.Models;

namespace RecoverX.Core.Services
{
    /// <summary>
    /// Phase 2 drive service. Starts from the Phase 1 DriveInfo list (unchanged) and adds SSD/HDD, bus type, vendor
    /// and TRIM details from the native engine. If the engine is missing or fails, the Phase 1 result is returned as-is.
    /// </summary>
    public sealed class EnrichedDriveService : IDriveService
    {
        private readonly IDriveService _basic;
        private readonly IEngineClient _engine;
        private readonly IAppLogger _log;

        public EnrichedDriveService(IDriveService basic, IEngineClient engine, IAppLogger log)
        {
            _basic = basic; _engine = engine; _log = log;
        }

        public IReadOnlyList<DriveModel> GetDrives()
        {
            IReadOnlyList<DriveModel> drives = _basic.GetDrives();
            if (!_engine.IsAvailable) return drives;

            IReadOnlyList<VolumeHardwareInfo> hardware;
            try { hardware = _engine.EnumerateVolumes(); }
            catch (EngineException ex)
            {
                _log.Warn("Native hardware detection failed, using basic detection: " + ex.Message);
                return drives;
            }

            var byLetter = new Dictionary<string, VolumeHardwareInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in hardware) byLetter[h.Letter] = h;
            foreach (var d in drives)
            {
                VolumeHardwareInfo hw;
                if (byLetter.TryGetValue(d.Letter, out hw)) DriveClassifier.Apply(d, hw);
            }
            return drives;
        }
    }
}
