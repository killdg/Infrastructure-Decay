using Mono.Cecil;
using InfrastructureDecay;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
    Console.WriteLine("PASS " + message);
    checks++;
}

var schedule = new OutageSchedule();
Check(!schedule.Tick(0,
     180,
     180,
     30,
     0.33,
     0.99),
     "First eligibility schedules an outage without immediately blacking out the map");
Check(!schedule.Tick(179, 180, 180, 30, 0.33, 0), "No outage before the selected opening delay");
Check(schedule.Tick(180, 180, 180, 30, 0.33, 0.2) && schedule.Active, "Opening window obeys its configured chance");
Check(schedule.Ends == 210 && !schedule.Tick(209, 180, 180, 30, 0.33, 0), "Active outage retains its original deadline");
Check(schedule.Tick(210, 180, 180, 30, 0.33, 0) && !schedule.Active, "Power returns at the deadline");
Check(!schedule.Tick(389, 180, 180, 30, 0.33, 0), "Repeat interval begins after power restoration");
Check(!schedule.Tick(390, 180, 180, 30, 0.33, 0.5) && !schedule.Active, "Failed roll leaves power available");
Check(!schedule.Tick(391, 180, 180, 30, 0.33, 0), "Failed roll does not retry every frame");
Check(schedule.Tick(570,
     180,
     180,
     120,
     0.33,
     0.2) && schedule.Ends == 690,
     "Successful repeat supports maximum default duration");
schedule.Reset();
Check(!schedule.Active && !schedule.Tick(0, 300, 360, 30, 0.33, 0), "Reset clears active lease and prior raid timing");
schedule.Start(10, 5);
Check(schedule.Active && schedule.Started == 10 && schedule.Ends == 15, "Manual test starts exactly one bounded outage");
foreach (bool recovered in new[] { false, true })
{
    int count = recovered ? PowerFlickerPatterns.RecoveredCount : PowerFlickerPatterns.OutageCount;
    var signatures = new HashSet<string>();
    for (int pattern = 0; pattern < count; pattern++)
    {
        var gains = Enumerable.Range(0, 101).Select(step => PowerFlickerPatterns.Gain(pattern, recovered, step / 100d)).ToArray();
        Check(gains.All(gain => gain >= 0 && gain <= 1), "Pattern brightness remains bounded: " + recovered + "/" + pattern);
        Check(gains.All(gain => gain == 0 || gain == 1),
            "Electrical flicker switches abruptly without glow interpolation: " + recovered + "/" + pattern);
        Check(gains[0] == 1 && gains[100] == (recovered ? 1 : 0),
             "Pattern starts normally and ends in the required power state: " + recovered + "/" + pattern);
        signatures.Add(string.Join(",",
             gains.Select(gain => gain.ToString("F3",
             System.Globalization.CultureInfo.InvariantCulture))));
    }
    Check(signatures.Count == count, "Every pattern has a distinct brightness envelope: " + recovered);
}

if (args.Length == 2)
{
    using var native = AssemblyDefinition.ReadAssembly(args[0] + "/EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll");
    var player = native.MainModule.Types.Single(type => type.FullName == "EFT.Player");
    Check(player.Methods.Any(method => method.Name == "StartInteraction" && method.Parameters.Count == 3 && method.Parameters[0].ParameterType.Name == "WorldInteractiveObject"),
         "Native start-interaction hook resolves before movement-state locking");
    Check(player.Methods.Any(method => method.Name == "ExecuteInteraction" && method.Parameters.Count == 2),
         "Native immediate-interaction hook resolves");
    var interactive = native.MainModule.Types.Single(type => type.FullName == "EFT.Interactive.WorldInteractiveObject");
    Check(interactive.Methods.Any(method => method.Name == "CanStartInteraction" && method.ReturnType.FullName == "System.Boolean"),
         "Native availability hook returns a refusal before interaction start");
    using var built = AssemblyDefinition.ReadAssembly(args[1]);
    Check(!built.MainModule.Types.Any(type => type.Methods.Any(method => method.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "HarmonyPatch" && attribute.ConstructorArguments.Any(argument => argument.Value?.ToString() == "Interact")))),
         "No late Interact method-level veto remains");
    Check(built.MainModule.Types.Any(type => type.Name == "PoweredControlAvailability") && built.MainModule.Types.Any(type => type.Name == "PoweredControlEntry"),
         "Built repair includes both availability and early-entry protection");
    var cache = built.MainModule.Types.Single(type => type.Name == "MapLightCache");
    var materialTarget = cache.NestedTypes.Single(type => type.Name == "MaterialSlotTarget");
    var materialCalls = materialTarget.Methods.Where(method => method.HasBody)
        .SelectMany(method => method.Body.Instructions)
        .Select(instruction => instruction.Operand).OfType<MethodReference>().ToArray();
    Check(materialCalls.Any(method => method.Name == "SetPropertyBlock" && method.Parameters.Count == 2),
        "Material controls use per-slot property blocks for multi-material fixtures");
    Check(!materialCalls.Any(method => method.DeclaringType.FullName == "UnityEngine.Material"
        && (method.Name.StartsWith("Set") || method.Name == "set_shader")),
        "Material controls never mutate shared material assets");
    var restore = materialTarget.Methods.Single(method => method.Name == "Restore");
    Check(restore.Body.Instructions.Any(instruction => instruction.OpCode.Code == Mono.Cecil.Cil.Code.Ldnull),
        "Restoration can remove temporary slot overrides and resume native renderer inheritance");
    var group = built.MainModule.Types.Single(type => type.Name == "LightSourceGroup");
    Check(group.Fields.Count(field => field.IsStatic) == 8,
        "All eight source groups are present in the built test client");
}



var openingFailure = new OutageSchedule();
openingFailure.Tick(0, 180, 180, 30, 0.2, 0);
Check(!openingFailure.Tick(180, 180, 180, 30, 0.2, 0.21)
    && !openingFailure.Active, "Day opening roll can leave power on");
Check(!openingFailure.Tick(181, 180, 180, 30, 0.6, 0),
    "Failed opening roll waits for next scheduled window");
Check(openingFailure.Tick(360, 180, 180, 30, 0.6, 0.59)
    && openingFailure.Active, "Night chance applies at next scheduled window");

Console.WriteLine($"{checks} scheduling, pattern and native-contract checks passed.");
