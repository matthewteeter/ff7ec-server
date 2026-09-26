// FF7EC build 24813881 (GameAssembly.dll SHA-256
// FAA51661BDA8BD7E90FBE122309A910E6D857B0C6F45508DA04C41EF45536CB6).
// Damage Challenge normally requires the retired authoritative battle server.
// Skip that connection for this battle type and select the existing local battle path.
const eventLog = new File('E:\\FF7EC-Server\\offline-client-hook.log', 'a');
function trace(message) {
  eventLog.write(`${message}\n`);
  eventLog.flush();
  send(message);
}

const gameAssembly = Process.getModuleByName('GameAssembly.dll');
const createDamageChallengeParameter = gameAssembly.base.add(0x1d02a80);
const getUsesBattleServer = gameAssembly.base.add(0x1142d40);
const getDamageChallengeBattleInfo = new NativeFunction(
  gameAssembly.base.add(0x7cc270),
  'pointer',
  ['pointer']
);
const usesBattleServerFieldOffset = 0x9c;
const connectBattleServer = gameAssembly.base.add(0x1cf34f0);
const completedUniTask = new Uint8Array(16);
let overrideLogged = false;
let originalConnectBattleServer;

function logOverride() {
  if (overrideLogged) return;
  overrideLogged = true;
  trace('[offline] Damage Challenge configured for local battle execution');
}

Interceptor.attach(createDamageChallengeParameter, {
  onLeave(parameter) {
    if (parameter.isNull()) return;
    parameter.add(usesBattleServerFieldOffset).writeU8(0);
    logOverride();
  }
});

// The trivial property may be inlined at some call sites, but this fallback also covers
// parameters created before the factory hook was installed.
Interceptor.attach(getUsesBattleServer, {
  onEnter(args) {
    this.isDamageChallenge = !getDamageChallengeBattleInfo(args[0]).isNull();
  },
  onLeave(result) {
    if (!this.isDamageChallenge) return;
    result.replace(0);
    logOverride();
  }
});

const connectBattleServerReplacement = new NativeCallback(function (
  result,
  presenter,
  cancellationToken,
  battleType,
  partyId,
  staminaBoostType,
  targetId,
  methodInfo
) {
  if (battleType === 15) {
    result.writeByteArray(completedUniTask);
    trace('[offline] Skipped Damage Challenge battle-server connection');
    return result;
  }

  return originalConnectBattleServer(
    result,
    presenter,
    cancellationToken,
    battleType,
    partyId,
    staminaBoostType,
    targetId,
    methodInfo
  );
}, 'pointer', ['pointer', 'pointer', 'uint64', 'int', 'int64', 'int', 'int64', 'pointer']);

const originalConnectBattleServerPointer = Interceptor.replaceFast(
  connectBattleServer,
  connectBattleServerReplacement
);
originalConnectBattleServer = new NativeFunction(
  originalConnectBattleServerPointer,
  'pointer',
  ['pointer', 'pointer', 'uint64', 'int', 'int64', 'int', 'int64', 'pointer']
);

for (const requestRva of [0xe17480, 0xe175a0]) {
  Interceptor.attach(gameAssembly.base.add(requestRva), {
    onEnter() {
      trace('[offline] WARNING: match/session was requested; Damage Challenge bypass did not take effect');
    }
  });
}

trace('[offline] Damage Challenge local-battle hook installed');
