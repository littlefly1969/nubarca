// Entry redirect: send the user to their default destination.
import React from 'react';
import { Redirect } from 'expo-router';
import { useSession } from '../src/session/SessionProvider';

export default function Index(): React.JSX.Element | null {
  const session = useSession();
  if (session.status === 'restoring') return null;
  if (session.status === 'authed') return <Redirect href="/(tabs)/photos" />;
  return <Redirect href="/login" />;
}
