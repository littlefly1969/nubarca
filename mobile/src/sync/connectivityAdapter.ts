// Thin expo-network adapter for the sync network policy.
//
// The policy decision itself (Wi-Fi-only vs cellular) lives in syncPolicy;
// this adapter only reports what the platform sees and forwards change
// events so a Waiting-for-Wi-Fi engine wakes without polling aggressively.

import * as Network from 'expo-network';
import type { ConnectivityPort, NetworkState } from './syncTypes.ts';

function mapState(state: Network.NetworkState): NetworkState {
  if (state.type === Network.NetworkStateType.WIFI) return { kind: 'wifi' };
  if (state.type === Network.NetworkStateType.CELLULAR) return { kind: 'cellular' };
  if (state.type === Network.NetworkStateType.NONE) return { kind: 'none' };
  return { kind: 'unknown' };
}

class ExpoConnectivityPort implements ConnectivityPort {
  async getNetworkState(): Promise<NetworkState> {
    try {
      return mapState(await Network.getNetworkStateAsync());
    } catch {
      // Fail closed: unknown connectivity never triggers uploads.
      return { kind: 'unknown' };
    }
  }

  onNetworkChange(listener: (state: NetworkState) => void): () => void {
    const subscription = Network.addNetworkStateListener((state) => {
      listener(mapState(state));
    });
    return () => subscription.remove();
  }
}

export const connectivityPort: ConnectivityPort = new ExpoConnectivityPort();
