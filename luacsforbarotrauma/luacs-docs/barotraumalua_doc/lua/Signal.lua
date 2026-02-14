-- luacheck: ignore 111

--[[--
Barotrauma Signal struct with some additional functions and fields

Barotrauma source code: [Signal.cs](https://github.com/evilfactory/Barotrauma-lua-attempt/blob/master/Barotrauma/BarotraumaShared/SharedSource/Items/Components/Signal/Signal.cs)
]]
-- @code Signal
-- @pragma nostrip

Signal = {}

--- Instantiates a new Signal.
-- @treturn Signal
-- @realm shared 
function Signal(stringValue, stepsTaken, characterSender, itemSource, power, strength) end

---
-- value, String value of the signal.
-- @realm shared
-- @string value

---
-- stepsTaken = 1
-- @realm shared
-- @number stepsTaken

---
-- sender = nil
-- @realm shared
-- @Character sender

---
-- source = nil
-- @realm shared
-- @Item source

---
-- power = 0
-- @realm shared
-- @number power

---
-- strength = 1
-- @realm shared
-- @number strength