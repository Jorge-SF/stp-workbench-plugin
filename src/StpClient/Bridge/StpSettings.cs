// Per-user settings of the STP UG plugin: %AppData%\Maptek\StpUG\settings.xml.

using System;
using System.IO;
using System.Xml.Linq;

namespace Maptek.Vulcan.Stp.Bridge
{
    internal sealed class StpSettings
    {
        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Maptek", "StpUG");

        public static string FilePath => Path.Combine(Folder, "settings.xml");

        /// <summary>Folder holding the entry scripts; empty means the default (see StpScriptLocator).</summary>
        public string ScriptsFolder { get; set; } = "";

        /// <summary>Reads the settings; a missing or unreadable file gives the defaults.</summary>
        public static StpSettings Load()
        {
            var s = new StpSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                var root = XDocument.Load(FilePath).Root;
                s.ScriptsFolder = (string)root?.Element("ScriptsFolder") ?? "";
            }
            catch (Exception)
            {
                // defaults; the settings window shows what is in use
            }
            return s;
        }

        /// <summary>Writes the settings through a temporary file, so a failed write leaves the old file.</summary>
        public void Save()
        {
            Directory.CreateDirectory(Folder);
            var doc = new XDocument(new XElement("StpSettings", new XElement("ScriptsFolder", ScriptsFolder ?? "")));
            string tmp = FilePath + ".tmp";
            doc.Save(tmp);
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
    }
}
