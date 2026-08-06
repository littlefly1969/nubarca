import { FolderBrowser } from '../components/FolderBrowser';
import { useAuth } from '../auth/useAuth';

// Authenticated home page. Renders the folder browser as the main view.
// Upload, gallery, trash, and share-link tools will be added as siblings or
// dedicated routes in upcoming slices.
export function HomePage() {
  const { state } = useAuth();
  if (state.status !== 'authed') {
    return null;
  }

  return <FolderBrowser />;
}
