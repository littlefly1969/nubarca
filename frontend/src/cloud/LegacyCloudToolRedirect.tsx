import { Navigate } from 'react-router';
import { cloudToolUrl, type CloudToolId } from './cloudTools';

// Backward compatibility for bookmarks to the standalone pages these tools used
// to have (/upload, /tv-devices). They redirect to the canonical Cloud
// Functions URL for the matching tool.
//
// `replace` keeps the dead legacy entry out of the history stack, so Back from
// the hub goes wherever the user actually came from rather than bouncing
// through the redirect. Query parameters are dropped on purpose: neither legacy
// route had any documented meaning for them.
export function LegacyCloudToolRedirect({ tool }: { tool: CloudToolId }) {
  return <Navigate to={cloudToolUrl(tool)} replace />;
}
