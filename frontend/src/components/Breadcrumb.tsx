export interface BreadcrumbEntry {
  // null only for the synthetic "Home" root entry.
  id: string | null;
  name: string;
}

interface BreadcrumbProps {
  trail: ReadonlyArray<BreadcrumbEntry>;
  onNavigate(index: number): void;
  disabled?: boolean;
}

// Renders trail entries as buttons separated by chevrons. The current
// (last) entry is rendered as plain text and not clickable.
export function Breadcrumb({ trail, onNavigate, disabled }: BreadcrumbProps) {
  return (
    <nav className="breadcrumb" aria-label="Folder path">
      {trail.map((entry, index) => {
        const isLast = index === trail.length - 1;
        return (
          <span key={entry.id ?? 'root'} className="breadcrumb-item">
            {isLast ? (
              <span aria-current="location">{entry.name}</span>
            ) : (
              <button
                type="button"
                className="breadcrumb-link"
                onClick={() => onNavigate(index)}
                disabled={disabled === true}
              >
                {entry.name}
              </button>
            )}
            {!isLast && <span className="breadcrumb-sep" aria-hidden="true">›</span>}
          </span>
        );
      })}
    </nav>
  );
}
