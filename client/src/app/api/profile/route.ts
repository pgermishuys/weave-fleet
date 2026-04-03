/**
 * GET /api/profile — returns the active profile name and whether it's the default.
 * Used by the frontend to display the profile badge at runtime (especially in
 * production standalone mode where NEXT_PUBLIC_WEAVE_PROFILE is baked at build time
 * and may not reflect the runtime profile).
 */

import { NextResponse } from "next/server";
import { getProfileName, isDefaultProfile } from "@/lib/server/profile";

export const dynamic = "force-dynamic";

export function GET() {
  return NextResponse.json({
    name: getProfileName(),
    isDefault: isDefaultProfile(),
  });
}
