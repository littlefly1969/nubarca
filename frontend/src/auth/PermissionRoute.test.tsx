import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import { PERMISSIONS } from '@nubarca/api-client';
import { AuthedWrapper } from '../test-utils';
import { PermissionRoute } from './PermissionRoute';

afterEach(cleanup);

function renderGuard(permissions: readonly string[], required: string[]) {
  return render(
    <AuthedWrapper permissions={permissions}>
      <MemoryRouter>
        <PermissionRoute permissions={required as never}>
          <div data-testid="protected">people page</div>
        </PermissionRoute>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

describe('PermissionRoute', () => {
  it('renders the destination when the permission is held', () => {
    renderGuard([PERMISSIONS.peopleAccess], [PERMISSIONS.peopleAccess]);
    expect(screen.getByTestId('protected')).toBeInTheDocument();
  });

  it('shows a clean forbidden state instead of the destination', () => {
    // The point of the guard: without it a direct navigation renders the page,
    // which fires its API call, gets 403, and shows a broken partial state.
    renderGuard([], [PERMISSIONS.peopleAccess]);
    expect(screen.queryByTestId('protected')).not.toBeInTheDocument();
    expect(screen.getByTestId('forbidden-page')).toBeInTheDocument();
    expect(screen.getByText('Accesso non consentito')).toBeInTheDocument();
  });

  it('offers a way back rather than a dead end', () => {
    renderGuard([], [PERMISSIONS.peopleAccess]);
    expect(screen.getByRole('link', { name: 'Torna alla home' })).toHaveAttribute('href', '/');
  });

  it('requires every permission for a composite destination', () => {
    // Laboratory sections: the shell permission is required alongside the
    // section's own, exactly as the server's composite policy demands.
    renderGuard(
      [PERMISSIONS.laboratoryPlates],
      [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates],
    );
    expect(screen.getByTestId('forbidden-page')).toBeInTheDocument();

    cleanup();
    renderGuard(
      [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates],
      [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates],
    );
    expect(screen.getByTestId('protected')).toBeInTheDocument();
  });
});
