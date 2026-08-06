import type { MobileMessageKey } from './it';

// English dictionary for the mobile app, typed against the Italian keys. Any key
// omitted here falls back to Italian at render time — no raw keys are shown.
const en: Partial<Record<MobileMessageKey, string>> = {
  'common.loading': 'Loading…',
  'common.retry': 'Retry',
  'common.signOut': 'Sign out',
  'common.back': '‹ Back',

  'app.restoring': 'Restoring session…',

  'login.sessionExpired': 'Your session expired. Please sign in again.',
  'login.apiBaseUrl': 'API Base URL',
  'login.email': 'Email',
  'login.password': 'Password',
  'login.signIn': 'Sign In',

  'gallery.home': 'Home',
  'gallery.photos': 'Photos',
  'gallery.files': 'Files',
  'gallery.loadMore': 'Load more',
  'gallery.loadMoreError': "Couldn't load more — tap to retry",
  'gallery.noPhotos': 'No photos yet.',
  'gallery.folderEmpty': 'This folder is empty.',
  'gallery.pullToRefresh': 'Pull down to refresh.',
  'gallery.whatPhotos': 'photos',
  'gallery.whatFolder': 'this folder',
  'gallery.loadErrorHttp': "Couldn't load {what} (error {status}).",
  'gallery.loadErrorNetwork': "Couldn't load {what}. Check your connection.",
};

export default en;
