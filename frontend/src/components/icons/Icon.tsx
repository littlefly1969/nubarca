// One tiny, dependency-free icon set for the app shell and the control
// surfaces this slice touches.
//
// Every glyph is a 24x24 stroked path drawn with `currentColor`, so it inherits
// the surrounding text colour and is automatically correct in both themes with
// no per-theme asset. Icons are decorative: they are rendered
// `aria-hidden="true"` and every icon-only control carries its own accessible
// name, so nothing here is ever the sole carrier of meaning.

export type IconName =
  | 'files'
  | 'library'
  | 'albums'
  | 'shared-albums'
  | 'people'
  | 'plates'
  | 'aesthetics'
  | 'shares'
  | 'functions'
  | 'private'
  | 'trash'
  | 'admin'
  | 'import'
  | 'jobs'
  | 'users'
  | 'account'
  | 'signout'
  | 'menu'
  | 'close'
  | 'chevron-left'
  | 'chevron-right'
  | 'search'
  | 'filter'
  | 'sort'
  | 'upload'
  | 'tv'
  | 'calendar'
  | 'archive'
  | 'photo'
  | 'video'
  | 'media'
  | 'download'
  | 'edit'
  | 'album-add'
  | 'similar'
  | 'explore'
  | 'info';

const PATHS: Record<IconName, string> = {
  files: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z',
  library: 'M4 5h6v6H4zM14 5h6v6h-6zM4 13h6v6H4zM14 13h6v6h-6z',
  albums: 'M7 4h13a1 1 0 0 1 1 1v13M4 7h13a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1z',
  // An album with an incoming arrow: somebody else's collection, handed to you.
  'shared-albums': 'M4 7h11a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1zM20 4v8M20 12l-3-3M20 12l3-3',
  people: 'M16 19v-1a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v1M12 7a3 3 0 1 1-6 0 3 3 0 0 1 6 0zM17 11a3 3 0 1 0 0-6M21 19v-1a4 4 0 0 0-3-3.9',
  plates: 'M3 9h18v6H3zM6 12h2M11 12h2M16 12h2',
  aesthetics: 'M12 3l2.4 5.6L20 10l-4 4 1 6-5-2.8L7 20l1-6-4-4 5.6-1.4z',
  shares: 'M8 12a2 2 0 1 1-4 0 2 2 0 0 1 4 0zM20 6a2 2 0 1 1-4 0 2 2 0 0 1 4 0zM20 18a2 2 0 1 1-4 0 2 2 0 0 1 4 0zM7.7 11l8.6-4M7.7 13l8.6 4',
  functions: 'M5 5h5v5H5zM14 5h5v5h-5zM5 14h5v5H5zM14 14h5v5h-5z',
  private: 'M6 11V8a6 6 0 0 1 12 0v3M5 11h14v9H5zM12 15v2',
  trash: 'M4 7h16M9 7V5h6v2M6 7l1 13h10l1-13M10 11v6M14 11v6',
  admin: 'M12 3l8 4v5c0 5-3.4 8-8 9-4.6-1-8-4-8-9V7z',
  import: 'M12 3v11M8 10l4 4 4-4M4 18h16',
  jobs: 'M12 7v5l3 2M12 21a9 9 0 1 1 0-18 9 9 0 0 1 0 18z',
  users: 'M16 19v-1a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v1M12 7a3 3 0 1 1-6 0 3 3 0 0 1 6 0zM21 19v-1a4 4 0 0 0-3-3.9M17 11a3 3 0 1 0 0-6',
  account: 'M20 20v-1a5 5 0 0 0-5-5H9a5 5 0 0 0-5 5v1M15.5 7.5a3.5 3.5 0 1 1-7 0 3.5 3.5 0 0 1 7 0z',
  signout: 'M10 20H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h5M16 8l4 4-4 4M20 12H9',
  menu: 'M4 7h16M4 12h16M4 17h16',
  close: 'M6 6l12 12M18 6L6 18',
  'chevron-left': 'M14 6l-6 6 6 6',
  'chevron-right': 'M10 6l6 6-6 6',
  search: 'M11 18a7 7 0 1 1 0-14 7 7 0 0 1 0 14zM16.5 16.5L21 21',
  filter: 'M4 6h16l-6 7v6l-4-2v-4z',
  sort: 'M7 4v16M7 20l-3-3M7 20l3-3M17 20V4M17 4l-3 3M17 4l3 3',
  upload: 'M12 16V4M8 8l4-4 4 4M4 20h16',
  tv: 'M4 6h16v10H4zM9 20h6M12 16v4',
  calendar: 'M4 6h16v14H4zM4 10h16M9 4v3M15 4v3',
  archive: 'M3 6h18v4H3zM5 10v10h14V10M10 14h4',
  photo: 'M4 5h16v14H4zM4 16l4.5-4.5 3 3L15 11l5 5M15.5 8.5a1 1 0 1 1-2 0 1 1 0 0 1 2 0z',
  video: 'M4 6h11v12H4zM15 10l5-3v10l-5-3z',
  media: 'M4 5h16v14H4zM4 15l4-4 3 3 3-3 6 6',
  download: 'M12 4v11M8 11l4 4 4-4M5 20h14',
  edit: 'M4 20h4L20 8l-4-4L4 16z',
  'album-add': 'M4 6h9a1 1 0 0 1 1 1v11a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1zM18 5v8M14.5 9h7',
  similar: 'M4 5h7v7H4zM14 12h6v6h-6zM11.5 8.5h3M13 10.5V6.5',
  explore: 'M12 21a9 9 0 1 1 0-18 9 9 0 0 1 0 18zM15.5 8.5l-2 5-5 2 2-5z',
  info: 'M12 21a9 9 0 1 1 0-18 9 9 0 0 1 0 18zM12 11v6M12 8h.01',
};

export interface IconProps {
  name: IconName;
  // Visual size in px. Defaults to the 18px used across the shell.
  size?: number;
  className?: string;
}

export function Icon({ name, size = 18, className }: IconProps) {
  return (
    <svg
      className={className ? `nc-icon ${className}` : 'nc-icon'}
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={PATHS[name]} />
    </svg>
  );
}
