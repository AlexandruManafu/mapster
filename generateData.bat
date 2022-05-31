@echo off
cd DataPipeline/MapFeatureGenerator
dotnet run -- -i "../../Tests/TestOSMDataReader/MapData/andorra-10032022.osm.pbf" -o "../../andorra.bin"

pause
pause