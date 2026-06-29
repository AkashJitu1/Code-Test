namespace SKFProductAssistant.Models;

// Documents the JSON shape that LookupAttribute returns from the AI extraction call.
//
// Single match:   {"found": true,  "name": "Width", "value": "15", "unit": "mm"}
// Not found:      {"found": false, "message": "Attribute 'xyz' not found in datasheet"}
// Ambiguous:      {"found": true,  "multiple": true, "matches": [{...}, ...]}
