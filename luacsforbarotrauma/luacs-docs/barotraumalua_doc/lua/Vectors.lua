-- luacheck: ignore 111

--[[--
Class providing vector functionality.
These are the XNA Vectors, you can find all the functions and fields here: <br>
<a href="https://docs.microsoft.com/en-us/previous-versions/windows/silverlight/dotnet-windows-silverlight/bb199660(v=xnagamestudio.35)">XNA Vector2</a> <br>
<a href="https://docs.microsoft.com/en-us/previous-versions/windows/silverlight/dotnet-windows-silverlight/bb199670(v=xnagamestudio.35)">XNA Vector3</a> <br>
<a href="https://docs.microsoft.com/en-us/previous-versions/windows/silverlight/dotnet-windows-silverlight/bb199679(v=xnagamestudio.35)">XNA Vector4</a> <br>

Access them via Vector2.\*, Vector3.\*, Vector4.\* <br>
]]
-- @code Vectors

local Vector2 = {}
local Vector3 = {}
local Vector4 = {}

--- Create Vector2
-- @treturn Vector2
-- @realm shared 
function Vector2(X, Y) end

--- Create Vector3
-- @treturn Vector3
-- @realm shared 
function Vector3(X, Y, Z) end

--- Create Vector4
-- @treturn Vector4
-- @realm shared 
function Vector4(X, Y, Z, W) end