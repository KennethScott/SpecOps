using System.Collections.Generic;

namespace SpecOps.Models
{
    public class ScriptParameter
    {
        /// <summary>
        /// Parameter name as it is used in the script
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// HTML5 compliant input element types (i.e. date, email, number, text, time)
        /// https://developer.mozilla.org/en-US/docs/Web/HTML/Element/input
        /// </summary>
        public string Type { get; set; }

        /// <summary>
        /// Brief description of the parameter
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Optional text to be shown in the input element (if relevant)
        /// </summary>
        public string Placeholder { get; set; }

        /// <summary>
        /// Setting Required to true will generate the HTML5 compliant required attribute on the generated element
        /// </summary>
        public bool Required { get; set; }

        /// <summary>
        /// Minimum value to be used in conjunction with Type=range
        /// </summary>
        public int Min { get; set; }

        /// <summary>
        /// Maximum value to be used in conjunction with Type=range
        /// </summary>
        public int Max { get; set; }

        /// <summary>
        /// Step value to be used in conjunction with Type=range
        /// </summary>
        public int Step { get; set; }

        /// <summary>
        /// List of values to be used in conjunction with Type=range
        /// </summary>
        public IEnumerable<string> List { get; set; }

        /// <summary>
        /// Dictionary of key/value pairs for use with Type=select (dropdowns)
        /// </summary>
        public Dictionary<string, string> Options { get; set; }

        /// <summary>
        /// Regex pattern for input validation
        /// </summary>
        public string Pattern { get; set; }
        
    }
}
