-- luacheck: ignore 111

--[[--
Class providing filesystem functionality.
]]
-- @code File
-- @pragma nostrip

File = {}

--- Read contents from path
-- @treturn string file contents
-- @realm shared 
function File.Read(path) end

--- Write text to file
-- @realm shared 
function File.Write(path, text) end

--- Check if file exists.
-- @treturn bool
-- @realm shared 
function File.Exists(path) end

--- Check if directory exists.
-- @treturn bool
-- @realm shared 
function File.DirectoryExists(path) end

--- Check if directory exists.
-- @treturn table table containing all files
-- @realm shared 
function File.GetFiles(path) end

--- List all directories.
-- @treturn table table containing all directories
-- @realm shared 
function File.GetDirectories(path) end

--- Search directory for all files including sub directories.
-- @treturn table table containing all files
-- @realm shared 
function File.DirSearch(path) end