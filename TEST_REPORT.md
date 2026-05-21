# Test Report

Generated: 2026-05-21 21:46:36 +08:00

## Summary

- Result: Passed
- Test harness: `tests/GarageManagement.Tests`
- Total behavior checks: 17
- Command:

```powershell
& 'C:\Users\huangd\AppData\Local\Microsoft\dotnet\dotnet.exe' run --project tests\GarageManagement.Tests\GarageManagement.Tests.csproj
```

## Output

```text
All behavior tests passed.
```

## Behavior Checks

- `CheckInAssignsFirstAvailableSpotAndMarksItOccupied`
- `CheckInRejectsDuplicateActiveLicensePlate`
- `CheckOutFreesAssignedSpotAndRecordsTimestamp`
- `CheckOutRejectsUnknownActiveCar`
- `AvailableSpotQueryOnlyReturnsAvailableSpots`
- `SpotStatusCannotBeChangedWhenAnActiveCarOccupiesIt`
- `FloorsBaysAndSpotsCanBeAddedToTheGarageLayout`
- `DuplicateParkingSpotIdentifiersAreRejected`
- `SpotStatusCanBeUpdatedWhenNoActiveCarUsesTheSpot`
- `EntityFrameworkStorePersistsGarageLayoutSpotStatusAndCarHistory`
- `CarCanBeLookedUpByLicensePlateNumber`
- `SpotUseHistoryRecordIsNotSavedUntilArchiveRuns`
- `CompletedCarParkingRecordsAreArchivedToSpotUseHistory`
- `OversizedCarRequiresOversizedSpot`
- `CompactCarCanUseStandardSpot`
- `CheckOutCalculatesRoundedHourlyParkingFee`
- `InMemoryCheckInQueueProcessesSubmittedRequests`

## Notes

- The test harness is a console executable with custom assertions, so it does not emit TRX/JUnit XML.
- A passing run exits with code `0`; assertion or build failures exit non-zero.
